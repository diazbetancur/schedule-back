using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Media;
using Barbershop.Application.Storage;
using Barbershop.Domain.Media;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Configuration;
using Barbershop.Infrastructure.Media;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Barbershop.Tests.Features.Media;

public sealed class MediaAssetUploadTests : IDisposable
{
  private readonly AppDbContext _dbContext;
  private readonly FakeFileStorageService _fileStorageService;
  private readonly FakeImageTranscoder _imageTranscoder;
  private readonly IMediaAssetsService _mediaAssetsService;
  private readonly Guid _currentUserId;

  public MediaAssetUploadTests()
  {
    var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(dbOptions);
    _fileStorageService = new FakeFileStorageService();
    _imageTranscoder = new FakeImageTranscoder();

    var fileStorageOptions = Options.Create(new FileStorageOptions
    {
      MaxUploadBytes = 5 * 1024 * 1024,
      AllowedContentTypes =
        [
            "image/jpeg",
                "image/png",
                "image/webp",
                "image/gif",
                "application/pdf"
        ]
    });

    _mediaAssetsService = new MediaAssetManagementService(
        _dbContext,
        _fileStorageService,
        TimeProvider.System,
        fileStorageOptions,
        _imageTranscoder,
        NullLogger<MediaAssetManagementService>.Instance);

    var now = DateTime.UtcNow;
    var currentUser = new User("Media Admin", "media-admin@example.com", "hashed-password", now);
    _dbContext.Users.Add(currentUser);
    _dbContext.SaveChanges();
    _currentUserId = currentUser.Id;
  }

  public void Dispose()
  {
    _dbContext.Dispose();
  }

  [Fact]
  public async Task UploadAsync_PersistsPendingStateBeforeReadyTransition()
  {
    var pendingObserved = false;
    _fileStorageService.OnUploadAsync = async _ =>
    {
      var mediaAsset = await _dbContext.MediaAssets.SingleAsync();
      pendingObserved = mediaAsset.Status == MediaAssetStatus.Pending;
    };

    using var stream = CreateStream();

    var response = await _mediaAssetsService.UploadAsync(
        _currentUserId,
        [RoleNames.Admin],
        new MediaAssetUploadRequest("photo.png", "image/png", stream.Length, MediaAssetPurpose.StaffPhoto, stream));

    Assert.True(pendingObserved);
    Assert.Equal(MediaAssetStatus.Ready, response.Status);
    Assert.NotNull(response.PublicUrl);

    var storedAsset = await _dbContext.MediaAssets.SingleAsync();
    Assert.Equal(MediaAssetStatus.Ready, storedAsset.Status);
    Assert.Equal(_currentUserId, storedAsset.UploadedByUserId);
    Assert.Null(storedAsset.FailureReason);
  }

  [Fact]
  public async Task UploadAsync_RejectsPurposeForStaffRole()
  {
    using var stream = CreateStream();

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _mediaAssetsService.UploadAsync(
            _currentUserId,
            [RoleNames.Staff],
            new MediaAssetUploadRequest("logo.png", "image/png", stream.Length, MediaAssetPurpose.Logo, stream)));

    Assert.Contains("purpose", exception.Errors.Keys);
    Assert.Empty(await _dbContext.MediaAssets.ToListAsync());
  }

  [Fact]
  public async Task UploadAsync_RejectsDangerousFileName()
  {
    using var stream = CreateStream();

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _mediaAssetsService.UploadAsync(
            _currentUserId,
            [RoleNames.Admin],
            new MediaAssetUploadRequest("..\\payload.exe", "application/pdf", stream.Length, MediaAssetPurpose.CustomerReference, stream)));

    Assert.Contains("fileName", exception.Errors.Keys);
    Assert.Empty(await _dbContext.MediaAssets.ToListAsync());
  }

  [Fact]
  public async Task UploadAsync_RejectsUnsupportedContentType()
  {
    using var stream = new MemoryStream([0x50, 0x4B, 0x03, 0x04, 0x10, 0x20]); // a ZIP archive

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _mediaAssetsService.UploadAsync(
            _currentUserId,
            [RoleNames.Customer],
            new MediaAssetUploadRequest("evidence.bin", "application/octet-stream", stream.Length, MediaAssetPurpose.CustomerReference, stream)));

    Assert.Contains("contentType", exception.Errors.Keys);
    Assert.Empty(await _dbContext.MediaAssets.ToListAsync());
  }

  [Fact]
  public async Task UploadAsync_WhenStorageFails_MarksAssetAsFailed()
  {
    _fileStorageService.ThrowOnUpload = true;
    using var stream = CreateStream();

    await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
        _mediaAssetsService.UploadAsync(
            _currentUserId,
            [RoleNames.Admin],
            new MediaAssetUploadRequest("banner.png", "image/png", stream.Length, MediaAssetPurpose.Banner, stream)));

    var storedAsset = await _dbContext.MediaAssets.SingleAsync();
    Assert.Equal(MediaAssetStatus.Failed, storedAsset.Status);
    Assert.False(string.IsNullOrWhiteSpace(storedAsset.FailureReason));
  }

  [Fact]
  public async Task UploadAsync_SkipsTranscodingForAlreadySupportedContentType()
  {
    _imageTranscoder.ThrowIfCalled = true;
    using var stream = CreateStream();

    var response = await _mediaAssetsService.UploadAsync(
        _currentUserId,
        [RoleNames.Staff],
        new MediaAssetUploadRequest("photo.png", "image/png", stream.Length, MediaAssetPurpose.StaffPhoto, stream));

    Assert.Equal("image/png", response.ContentType);
  }

  [Fact]
  public async Task UploadAsync_ConvertsUnsupportedImageContentTypeToJpeg()
  {
    var convertedBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
    _imageTranscoder.Result = new TranscodedImage(new MemoryStream(convertedBytes), convertedBytes.Length);
    using var stream = CreateHeicHeaderStream();

    var response = await _mediaAssetsService.UploadAsync(
        _currentUserId,
        [RoleNames.Staff],
        new MediaAssetUploadRequest("photo.heic", "image/heic", stream.Length, MediaAssetPurpose.StaffPhoto, stream));

    Assert.Equal("image/jpeg", response.ContentType);
    Assert.Equal("photo.jpg", response.FileName);
    Assert.Equal(convertedBytes.Length, response.SizeBytes);
  }

  [Fact]
  public async Task UploadAsync_WhenTranscodeFails_FallsBackToContentTypeValidationError()
  {
    _imageTranscoder.Result = null;
    using var stream = CreateHeicHeaderStream();

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _mediaAssetsService.UploadAsync(
            _currentUserId,
            [RoleNames.Staff],
            new MediaAssetUploadRequest("photo.heic", "image/heic", stream.Length, MediaAssetPurpose.StaffPhoto, stream)));

    Assert.Contains("contentType", exception.Errors.Keys);
  }

  /// <summary>
  /// BUG-078: product images and media uploads (purpose Other) accepted SVG because the transcoder rasterised it. With
  /// the real transcoder an SVG is now rejected for every purpose and nothing reaches storage.
  /// </summary>
  [Theory]
  [InlineData(MediaAssetPurpose.ProductImage)]
  [InlineData(MediaAssetPurpose.Other)]
  [InlineData(MediaAssetPurpose.Banner)]
  [InlineData(MediaAssetPurpose.Logo)]
  [InlineData(MediaAssetPurpose.AppIcon)]
  [InlineData(MediaAssetPurpose.StaffPhoto)]
  public async Task UploadAsync_WithTheRealTranscoder_RejectsSvg(MediaAssetPurpose purpose)
  {
    var service = new MediaAssetManagementService(
        _dbContext,
        _fileStorageService,
        TimeProvider.System,
        Options.Create(new FileStorageOptions()),
        new MagickImageTranscoder(NullLogger<MagickImageTranscoder>.Instance),
        NullLogger<MediaAssetManagementService>.Instance);
    using var svg = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
        "<svg xmlns='http://www.w3.org/2000/svg' width='4' height='4'><script>alert(1)</script></svg>"));

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        service.UploadAsync(
            _currentUserId,
            [RoleNames.Admin],
            new MediaAssetUploadRequest("x.svg", "image/svg+xml", svg.Length, purpose, svg)));

    Assert.Contains("contentType", exception.Errors.Keys);
    Assert.Empty(await _dbContext.MediaAssets.ToListAsync());
  }

  // ── BUG-020 / BUG-058: the real content decides, not the declared Content-Type or extension ──

  /// <summary>
  /// SONDA cases: HTML and SVG as image/png, an executable (MZ) as .png, text renamed .png and HTML declared as PDF.
  /// All of them used to be stored and published.
  /// </summary>
  [Theory]
  [InlineData("html", "page.png", "image/png", MediaAssetPurpose.Banner)]
  [InlineData("svg", "logo.png", "image/png", MediaAssetPurpose.Logo)]
  [InlineData("mz", "tool.png", "image/png", MediaAssetPurpose.Banner)]
  [InlineData("text", "notes.png", "image/png", MediaAssetPurpose.StaffPhoto)]
  [InlineData("html", "invoice.pdf", "application/pdf", MediaAssetPurpose.CustomerReference)]
  [InlineData("html", "qr.pdf", "application/pdf", MediaAssetPurpose.TipsQr)]
  [InlineData("svg", "product.jpg", "image/jpeg", MediaAssetPurpose.ProductImage)]
  public async Task UploadAsync_RejectsAFileWhoseContentIsNotAnAcceptedFormat(
      string kind, string fileName, string declaredContentType, MediaAssetPurpose purpose)
  {
    using var stream = new MemoryStream(DisguisedContent(kind));

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _mediaAssetsService.UploadAsync(
            _currentUserId,
            [RoleNames.Admin],
            new MediaAssetUploadRequest(fileName, declaredContentType, stream.Length, purpose, stream)));

    Assert.StartsWith("El archivo no es una imagen", Assert.Single(exception.Errors["contentType"]));
    Assert.Empty(await _dbContext.MediaAssets.ToListAsync());
  }

  [Fact]
  public async Task UploadAsync_ARealPdf_IsAcceptedForTheTipsQr()
  {
    using var stream = new MemoryStream(PdfBytes());

    var response = await _mediaAssetsService.UploadAsync(
        _currentUserId,
        [RoleNames.Staff],
        new MediaAssetUploadRequest("qr.pdf", "application/pdf", stream.Length, MediaAssetPurpose.TipsQr, stream));

    Assert.Equal("application/pdf", response.ContentType);
    Assert.EndsWith(".pdf", response.StorageKey);
  }

  [Theory]
  [InlineData("image/png")]
  [InlineData("application/pdf")]
  public async Task UploadAsync_ARealPdf_IsRejectedAsAStaffPhoto_WhateverItDeclares(string declaredContentType)
  {
    using var stream = new MemoryStream(PdfBytes());

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _mediaAssetsService.UploadAsync(
            _currentUserId,
            [RoleNames.Staff],
            new MediaAssetUploadRequest("photo.png", declaredContentType, stream.Length, MediaAssetPurpose.StaffPhoto, stream)));

    Assert.Contains("solo se aceptan imágenes", Assert.Single(exception.Errors["contentType"]));
    Assert.Empty(await _dbContext.MediaAssets.ToListAsync());
  }

  [Fact]
  public async Task UploadAsync_AJpegDeclaredAsPng_IsStoredWithItsRealType()
  {
    string? storedContentType = null;
    _fileStorageService.OnUploadAsync = file =>
    {
      storedContentType = file.ContentType;
      return Task.CompletedTask;
    };
    using var stream = new MemoryStream([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46]);

    var response = await _mediaAssetsService.UploadAsync(
        _currentUserId,
        [RoleNames.Admin],
        new MediaAssetUploadRequest("photo.png", "image/png", stream.Length, MediaAssetPurpose.Banner, stream));

    Assert.Equal("image/jpeg", response.ContentType);
    Assert.Equal("image/jpeg", storedContentType);
    Assert.EndsWith(".jpg", response.StorageKey);
  }

  /// <summary>BUG-058: the customer's photo travels as CustomerReference (which admits PDF) but must be an image.</summary>
  [Fact]
  public async Task UploadAsync_ImagesOnly_RejectsARealPdf_ThatThePurposeWouldOtherwiseAdmit()
  {
    using var forPhoto = new MemoryStream(PdfBytes());
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _mediaAssetsService.UploadAsync(
            _currentUserId,
            [RoleNames.Customer],
            new MediaAssetUploadRequest("foto.pdf", "application/pdf", forPhoto.Length, MediaAssetPurpose.CustomerReference, forPhoto, ImagesOnly: true)));
    Assert.Equal("Aquí solo se aceptan imágenes (JPG, PNG, WebP, GIF o HEIC).", Assert.Single(exception.Errors["contentType"]));

    using var asReference = new MemoryStream(PdfBytes());
    var reference = await _mediaAssetsService.UploadAsync(
        _currentUserId,
        [RoleNames.Customer],
        new MediaAssetUploadRequest("soporte.pdf", "application/pdf", asReference.Length, MediaAssetPurpose.CustomerReference, asReference));
    Assert.Equal("application/pdf", reference.ContentType);
  }

  /// <summary>
  /// HEIC/HEIF from phones keeps being converted to JPEG with the real transcoder, whatever the phone declares.
  /// Magick.NET has no HEVC encoder to build an iPhone-like sample, so the sample is a HEIF file written with its
  /// AV1 encoder and labelled with the generic HEIF brand (mif1): it goes through the same path as a HEIC upload
  /// (signature of the HEIF family, read as HEIC through libheif, re-encoded as JPEG).
  /// </summary>
  [Theory]
  [InlineData("image/heic")]
  [InlineData("image/heif")]
  [InlineData("application/octet-stream")]
  public async Task UploadAsync_WithTheRealTranscoder_ConvertsHeicToJpeg(string declaredContentType)
  {
    byte[] heic;
    using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Tomato, 16, 16) { Format = ImageMagick.MagickFormat.Avif })
    {
      heic = image.ToByteArray();
    }

    Assert.Equal("ftypavif", System.Text.Encoding.ASCII.GetString(heic, 4, 8));
    System.Text.Encoding.ASCII.GetBytes("mif1").CopyTo(heic, 8);
    Assert.Equal("image/heic", FileSignatures.DetectContentType(heic));

    var service = new MediaAssetManagementService(
        _dbContext,
        _fileStorageService,
        TimeProvider.System,
        Options.Create(new FileStorageOptions()),
        new MagickImageTranscoder(NullLogger<MagickImageTranscoder>.Instance),
        NullLogger<MediaAssetManagementService>.Instance);
    using var stream = new MemoryStream(heic);

    var response = await service.UploadAsync(
        _currentUserId,
        [RoleNames.Staff],
        new MediaAssetUploadRequest("IMG_0001.HEIC", declaredContentType, stream.Length, MediaAssetPurpose.StaffPhoto, stream));

    Assert.Equal("image/jpeg", response.ContentType);
    Assert.Equal("IMG_0001.jpg", response.FileName);
    Assert.EndsWith(".jpg", response.StorageKey);
  }

  // ── BUG-098: purpose outside the enum and file names with paths or invalid characters ──

  [Theory]
  [InlineData(999)]
  [InlineData(0)]
  [InlineData(-1)]
  public async Task UploadAsync_RejectsAPurposeOutsideTheEnum(int purpose)
  {
    using var stream = CreateStream();

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _mediaAssetsService.UploadAsync(
            _currentUserId,
            [RoleNames.Admin],
            new MediaAssetUploadRequest("photo.png", "image/png", stream.Length, (MediaAssetPurpose)purpose, stream)));

    Assert.Contains("purpose", exception.Errors.Keys);
    Assert.Empty(await _dbContext.MediaAssets.ToListAsync());
  }

  [Theory]
  [InlineData(@"C:\x.png", "x.png")]
  [InlineData(@"C:\Users\ana\Fotos\perfil.png", "perfil.png")]
  [InlineData("fotos/perfil.png", "perfil.png")]
  [InlineData("a?b.png", "a_b.png")]
  [InlineData("a<b>c:d|e*f\".png", "a_b_c_d_e_f_.png")]
  [InlineData("tab\there.png", "tab_here.png")]
  public async Task UploadAsync_SanitisesTheFileName(string fileName, string expected)
  {
    using var stream = CreateStream();

    var response = await _mediaAssetsService.UploadAsync(
        _currentUserId,
        [RoleNames.Admin],
        new MediaAssetUploadRequest(fileName, "image/png", stream.Length, MediaAssetPurpose.Banner, stream));

    Assert.Equal(expected, response.FileName);
    Assert.Equal(expected, (await _dbContext.MediaAssets.SingleAsync()).FileName);
  }

  [Theory]
  [InlineData(@"C:\tools\payload.exe")]
  [InlineData("../../script.js")]
  [InlineData("index.html")]
  public async Task UploadAsync_StillRejectsDangerousExtensions_AfterSanitising(string fileName)
  {
    using var stream = CreateStream();

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _mediaAssetsService.UploadAsync(
            _currentUserId,
            [RoleNames.Admin],
            new MediaAssetUploadRequest(fileName, "image/png", stream.Length, MediaAssetPurpose.Banner, stream)));

    Assert.Contains("fileName", exception.Errors.Keys);
  }

  [Fact]
  public async Task DeleteAsync_ArchivesReadyAssetAndDeletesObject()
  {
    var uploaded = await UploadValidAssetAsync();

    await _mediaAssetsService.DeleteAsync(uploaded.Id);

    var storedAsset = await _dbContext.MediaAssets.SingleAsync(asset => asset.Id == uploaded.Id);
    Assert.Equal(MediaAssetStatus.Archived, storedAsset.Status);
    Assert.Contains(uploaded.StorageKey, _fileStorageService.DeletedKeys);
  }

  [Fact]
  public async Task DeleteAsync_RejectsWhenAssetIsReferencedByStaffProfile()
  {
    var uploaded = await UploadValidAssetAsync();

    var now = DateTime.UtcNow;
    var staffUser = new User("Staff Member", $"staff-{Guid.NewGuid():N}@example.com", "hashed-password", now);
    var staffProfile = new StaffProfile(staffUser.Id, "Staff Member", 30, now);
    staffProfile.UpdateDetails("Staff Member", null, null, uploaded.Id, null, 30, true, now);

    _dbContext.Users.Add(staffUser);
    _dbContext.StaffProfiles.Add(staffProfile);
    await _dbContext.SaveChangesAsync();

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _mediaAssetsService.DeleteAsync(uploaded.Id));

    Assert.Contains("mediaAssetId", exception.Errors.Keys);
    Assert.DoesNotContain(uploaded.StorageKey, _fileStorageService.DeletedKeys);
  }

  private async Task<MediaAssetView> UploadValidAssetAsync()
  {
    using var stream = CreateStream();

    return await _mediaAssetsService.UploadAsync(
        _currentUserId,
        [RoleNames.Admin],
        new MediaAssetUploadRequest("banner.png", "image/png", stream.Length, MediaAssetPurpose.Banner, stream));
  }

  [Fact]
  public async Task UploadAsync_RawNonSeekableBody_IsBufferedBeforeReachingStorage()
  {
    bool? storageGotSeekableStream = null;
    long? storageContentLength = null;
    _fileStorageService.OnUploadAsync = file =>
    {
      storageGotSeekableStream = file.Content.CanSeek;
      storageContentLength = file.ContentLength;
      return Task.CompletedTask;
    };

    var payload = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
    using var body = new NonSeekableStream(payload);

    // The declared size (from Content-Length) is wrong on purpose: the real length must win.
    var response = await _mediaAssetsService.UploadAsync(
        _currentUserId,
        [RoleNames.Admin],
        new MediaAssetUploadRequest("photo.jpg", "image/jpeg", 999, MediaAssetPurpose.CustomerReference, body));

    Assert.True(storageGotSeekableStream);
    Assert.Equal(payload.Length, storageContentLength);
    Assert.Equal(payload.Length, response.SizeBytes);
  }

  [Fact]
  public async Task UploadAsync_RawBodyLargerThanTheLimit_IsRejected()
  {
    using var body = new NonSeekableStream(new byte[(5 * 1024 * 1024) + 1]);

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _mediaAssetsService.UploadAsync(
            _currentUserId,
            [RoleNames.Admin],
            new MediaAssetUploadRequest("photo.jpg", "image/jpeg", 100, MediaAssetPurpose.CustomerReference, body)));

    Assert.Contains("sizeBytes", exception.Errors.Keys);
    Assert.Empty(await _dbContext.MediaAssets.ToListAsync());
  }

  /// <summary>Behaves like HttpRequest.Body: forward-only, no Length.</summary>
  private sealed class NonSeekableStream(byte[] payload) : Stream
  {
    private readonly MemoryStream _inner = new(payload);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
      get => throw new NotSupportedException();
      set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
      if (disposing)
      {
        _inner.Dispose();
      }

      base.Dispose(disposing);
    }
  }

  private static MemoryStream CreateStream()
  {
    // A PNG signature: uploads are recognised by their first bytes (BUG-020).
    var payload = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01 };
    return new MemoryStream(payload, writable: false);
  }

  private static MemoryStream CreateHeicHeaderStream()
      => new([0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c', 0, 0], writable: false);

  private static byte[] PdfBytes()
      => System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Root 1 0 R >>\n%%EOF\n");

  private static byte[] DisguisedContent(string kind) => kind switch
  {
    "html" => System.Text.Encoding.UTF8.GetBytes("<!doctype html><html><body><script>alert(document.domain)</script></body></html>"),
    "svg" => System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"),
    "mz" => [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF],
    "text" => System.Text.Encoding.UTF8.GetBytes("esto es un archivo de texto renombrado"),
    _ => throw new ArgumentOutOfRangeException(nameof(kind))
  };

  private sealed class FakeFileStorageService : IFileStorageService
  {
    public bool ThrowOnUpload { get; set; }

    public Func<FileStorageObject, Task>? OnUploadAsync { get; set; }

    public List<string> DeletedKeys { get; } = [];

    public async Task<StoredFileResult> UploadAsync(FileStorageObject file, CancellationToken cancellationToken = default)
    {
      if (OnUploadAsync is not null)
      {
        await OnUploadAsync(file);
      }

      if (ThrowOnUpload)
      {
        throw new InvalidOperationException("Simulated storage upload failure.");
      }

      await file.Content.CopyToAsync(Stream.Null, cancellationToken);
      return new StoredFileResult(file.ObjectKey, new Uri($"https://assets.example.com/{file.ObjectKey}"));
    }

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
      DeletedKeys.Add(objectKey);
      return Task.CompletedTask;
    }

    public string? GetPublicUrl(string storageKey) => $"https://assets.example.com/{storageKey}";
  }

  private sealed class FakeImageTranscoder : IImageTranscoder
  {
    public bool ThrowIfCalled { get; set; }

    public TranscodedImage? Result { get; set; }

    public Task<TranscodedImage?> TryConvertToJpegAsync(
        Stream content, string contentType, CancellationToken cancellationToken = default)
    {
      if (ThrowIfCalled)
      {
        throw new InvalidOperationException("Transcoder should not have been called for this content type.");
      }

      return Task.FromResult(Result);
    }
  }
}
