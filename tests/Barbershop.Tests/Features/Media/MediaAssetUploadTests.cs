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
        _imageTranscoder);

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
    using var stream = CreateStream();

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
    using var stream = CreateStream();

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
    using var stream = CreateStream();

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _mediaAssetsService.UploadAsync(
            _currentUserId,
            [RoleNames.Staff],
            new MediaAssetUploadRequest("photo.heic", "image/heic", stream.Length, MediaAssetPurpose.StaffPhoto, stream)));

    Assert.Contains("contentType", exception.Errors.Keys);
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

  private static MemoryStream CreateStream()
  {
    var payload = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x10, 0x20 };
    return new MemoryStream(payload, writable: false);
  }

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
