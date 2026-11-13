using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Media;
using Barbershop.Application.Storage;
using Barbershop.Domain.Media;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Configuration;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Barbershop.Infrastructure.Media;

internal sealed class MediaAssetManagementService : IMediaAssetsService
{
  private static readonly HashSet<string> ImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/gif"
    };

  private static readonly HashSet<string> DocumentContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf"
    };

  private static readonly HashSet<string> BlockedFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".dll",
        ".bat",
        ".cmd",
        ".ps1",
        ".sh",
        ".msi",
        ".com",
        ".js",
        ".html",
        ".htm"
    };

  private static readonly IReadOnlyDictionary<MediaAssetPurpose, IReadOnlyCollection<string>> PurposeContentTypes =
      new Dictionary<MediaAssetPurpose, IReadOnlyCollection<string>>
      {
        [MediaAssetPurpose.Logo] = ImageContentTypes.ToArray(),
        [MediaAssetPurpose.AppIcon] = ImageContentTypes.ToArray(),
        [MediaAssetPurpose.Banner] = ImageContentTypes.ToArray(),
        [MediaAssetPurpose.StaffPhoto] = ImageContentTypes.ToArray(),
        [MediaAssetPurpose.TipsQr] = ImageContentTypes.Concat(DocumentContentTypes).ToArray(),
        [MediaAssetPurpose.CustomerReference] = ImageContentTypes.Concat(DocumentContentTypes).ToArray(),
        [MediaAssetPurpose.Other] = ImageContentTypes.Concat(DocumentContentTypes).ToArray(),
        [MediaAssetPurpose.ProductImage] = ImageContentTypes.ToArray()
      };

  private readonly AppDbContext _dbContext;
  private readonly IFileStorageService _fileStorageService;
  private readonly TimeProvider _timeProvider;
  private readonly FileStorageOptions _fileStorageOptions;
  private readonly HashSet<string> _allowedContentTypes;
  private readonly IImageTranscoder _imageTranscoder;
  private readonly ILogger<MediaAssetManagementService> _logger;

  public MediaAssetManagementService(
      AppDbContext dbContext,
      IFileStorageService fileStorageService,
      TimeProvider timeProvider,
      IOptions<FileStorageOptions> fileStorageOptions,
      IImageTranscoder imageTranscoder,
      ILogger<MediaAssetManagementService> logger)
  {
    _dbContext = dbContext;
    _fileStorageService = fileStorageService;
    _timeProvider = timeProvider;
    _fileStorageOptions = fileStorageOptions.Value;
    _allowedContentTypes = _fileStorageOptions.AllowedContentTypes
        .Select(NormalizeContentType)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    _imageTranscoder = imageTranscoder;
    _logger = logger;
  }

  public async Task<MediaAssetView> UploadAsync(
      Guid currentUserId,
      IReadOnlyCollection<string> roles,
      MediaAssetUploadRequest request,
      CancellationToken cancellationToken = default)
  {
    var originalContent = request.Content;
    request = await EnsureSeekableContentAsync(request, cancellationToken);
    using var bufferedContent = !ReferenceEquals(request.Content, originalContent) ? request.Content : null;

    request = request with { FileName = SanitizeFileName(request.FileName) };

    // The real type comes from the file's signature, never from the declared Content-Type (BUG-020, BUG-058).
    string? contentError = null;
    var detectedContentType = request.Content is null
        ? null
        : await FileSignatures.DetectContentTypeAsync(request.Content, cancellationToken);

    if (request.Content is not null && detectedContentType is null)
    {
      contentError = NotAcceptedMessage(request);
    }
    else if (detectedContentType is not null)
    {
      request = request with { ContentType = detectedContentType };
    }

    var contentBeforeTranscode = request.Content;
    if (detectedContentType is not null
        && FileSignatures.ConvertibleImages.Contains(detectedContentType)
        && request.SizeBytes > 0 && request.SizeBytes <= _fileStorageOptions.MaxUploadBytes)
    {
      request = await TryTranscodeToJpegAsync(request, cancellationToken);
      if (ReferenceEquals(request.Content, contentBeforeTranscode))
      {
        contentError = "No pudimos procesar esta imagen. Prueba con otra foto en JPG, PNG, WebP, GIF o HEIC.";
      }
    }

    using var transcodedContent = !ReferenceEquals(request.Content, contentBeforeTranscode) ? request.Content : null;

    ValidateUploadRequest(currentUserId, roles, request, contentError);
    await EnsureActiveUserExistsAsync(currentUserId, cancellationToken);

    var now = _timeProvider.GetUtcNow().UtcDateTime;
    var objectKey = BuildStorageKey(request.Purpose, request.FileName, request.ContentType, now);

    var mediaAsset = new MediaAsset(
        request.FileName,
        NormalizeContentType(request.ContentType),
        request.SizeBytes,
        objectKey,
        request.Purpose,
        currentUserId,
        now);

    _dbContext.MediaAssets.Add(mediaAsset);
    await _dbContext.SaveChangesAsync(cancellationToken);

    try
    {
      var storedFile = await _fileStorageService.UploadAsync(
          new FileStorageObject(mediaAsset.StorageKey, request.Content, mediaAsset.ContentType, mediaAsset.SizeBytes),
          cancellationToken);

      mediaAsset.MarkReady(storedFile.PublicUrl?.ToString(), _timeProvider.GetUtcNow().UtcDateTime);
      await _dbContext.SaveChangesAsync(cancellationToken);

      return Map(mediaAsset);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
      mediaAsset.MarkFailed(BuildFailureReason(exception), _timeProvider.GetUtcNow().UtcDateTime);
      await _dbContext.SaveChangesAsync(cancellationToken);

      _logger.LogError(
          exception,
          "Media upload failed for asset {MediaAssetId}, key {StorageKey}, purpose {Purpose}, content type {ContentType}.",
          mediaAsset.Id,
          mediaAsset.StorageKey,
          mediaAsset.Purpose,
          mediaAsset.ContentType);

      throw new ServiceUnavailableException("The media storage service is temporarily unavailable.", exception);
    }
  }

  /// <summary>
  /// Profile photos arrive as the raw request body, which is forward-only. The S3 client used for R2
  /// has to read the payload to sign it (and the transcoder reads it too), so a non-seekable stream
  /// made every raw upload fail. Buffer it once, bounded by MaxUploadBytes, and use the real length.
  /// </summary>
  private async Task<MediaAssetUploadRequest> EnsureSeekableContentAsync(
      MediaAssetUploadRequest request, CancellationToken cancellationToken)
  {
    if (request.Content is null || request.Content.CanSeek)
    {
      return request;
    }

    var maxBytes = _fileStorageOptions.MaxUploadBytes;
    var buffer = new MemoryStream();
    var chunk = new byte[81920];
    int read;

    while ((read = await request.Content.ReadAsync(chunk, cancellationToken)) > 0)
    {
      if (buffer.Length + read > maxBytes)
      {
        await buffer.DisposeAsync();
        throw new ValidationProblemException(new Dictionary<string, string[]>
        {
          ["sizeBytes"] = [$"SizeBytes must be less than or equal to {maxBytes} bytes."]
        });
      }

      buffer.Write(chunk, 0, read);
    }

    buffer.Position = 0;
    return request with { Content = buffer, SizeBytes = buffer.Length };
  }

  /// <summary>Converts a photo whose signature is HEIC/HEIF, AVIF, BMP or TIFF to JPEG; unchanged when it fails.</summary>
  private async Task<MediaAssetUploadRequest> TryTranscodeToJpegAsync(
      MediaAssetUploadRequest request, CancellationToken cancellationToken)
  {
    if (request.SizeBytes <= 0 || request.SizeBytes > _fileStorageOptions.MaxUploadBytes)
    {
      return request;
    }

    var converted = await _imageTranscoder.TryConvertToJpegAsync(request.Content, request.ContentType, cancellationToken);
    if (converted is null)
    {
      // The transcoder consumed the stream; rewind so validation and storage see the original bytes.
      if (request.Content.CanSeek)
      {
        request.Content.Position = 0;
      }

      return request;
    }

    var newFileName = string.IsNullOrWhiteSpace(request.FileName)
        ? "upload.jpg"
        : Path.ChangeExtension(request.FileName, ".jpg");

    return request with
    {
      FileName = newFileName,
      ContentType = "image/jpeg",
      SizeBytes = converted.SizeBytes,
      Content = converted.Content
    };
  }

  public async Task<IReadOnlyList<MediaAssetView>> GetAllAsync(CancellationToken cancellationToken = default)
  {
    return await _dbContext.MediaAssets
        .AsNoTracking()
        .OrderByDescending(asset => asset.CreatedAt)
        .Select(asset => new MediaAssetView(
            asset.Id,
            asset.FileName,
            asset.ContentType,
            asset.SizeBytes,
            asset.StorageKey,
            asset.PublicUrl,
            asset.Purpose,
            asset.Status,
            asset.UploadedByUserId,
            asset.FailureReason,
            asset.CreatedAt,
            asset.UpdatedAt))
        .ToListAsync(cancellationToken);
  }

  public async Task<MediaAssetView> GetByIdAsync(Guid mediaAssetId, CancellationToken cancellationToken = default)
  {
    ValidateMediaAssetId(mediaAssetId);

    var mediaAsset = await _dbContext.MediaAssets
        .AsNoTracking()
        .SingleOrDefaultAsync(asset => asset.Id == mediaAssetId, cancellationToken)
        ?? throw new KeyNotFoundException("The media asset was not found.");

    return Map(mediaAsset);
  }

  public async Task DeleteAsync(Guid mediaAssetId, CancellationToken cancellationToken = default)
  {
    ValidateMediaAssetId(mediaAssetId);

    // Under the asset's row lock, the same one linking a profile slot takes (BUG-019): a link committing at the same
    // time is either seen as a reference here or rejected because the asset was archived.
    await MediaAssetReferences.InTransactionAsync<object?>(_dbContext, async () =>
    {
      await MediaAssetReferences.LockAsync(_dbContext, [mediaAssetId], cancellationToken);
      await DeleteLockedAsync(mediaAssetId, cancellationToken);
      return null;
    }, cancellationToken);
  }

  private async Task DeleteLockedAsync(Guid mediaAssetId, CancellationToken cancellationToken)
  {
    var mediaAsset = await _dbContext.MediaAssets
        .SingleOrDefaultAsync(asset => asset.Id == mediaAssetId, cancellationToken)
        ?? throw new KeyNotFoundException("The media asset was not found.");

    if (mediaAsset.Status is MediaAssetStatus.Archived)
    {
      return;
    }

    if (mediaAsset.Status is not MediaAssetStatus.Ready)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["mediaAssetId"] = ["Only ready media assets can be deleted."]
      });
    }

    if (await IsMediaAssetReferencedAsync(mediaAssetId, cancellationToken))
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["mediaAssetId"] = ["The media asset is currently in use and cannot be deleted."]
      });
    }

    try
    {
      await _fileStorageService.DeleteAsync(mediaAsset.StorageKey, cancellationToken);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
      throw new ServiceUnavailableException($"Unable to delete media from storage: {exception.Message}");
    }

    mediaAsset.Archive(_timeProvider.GetUtcNow().UtcDateTime);
    await _dbContext.SaveChangesAsync(cancellationToken);
  }

  private void ValidateUploadRequest(
      Guid currentUserId,
      IReadOnlyCollection<string> roles,
      MediaAssetUploadRequest request,
      string? contentError)
  {
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    if (currentUserId == Guid.Empty)
    {
      errors["userId"] = ["CurrentUserId is required."];
    }

    if (request.Content is null)
    {
      errors["file"] = ["A file stream is required."];
    }

    if (string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Trim().Length > 260)
    {
      errors["fileName"] = ["FileName must be between 1 and 260 characters."];
    }
    else if (IsDangerousFileName(request.FileName))
    {
      errors["fileName"] = ["FileName contains dangerous characters or extension."];
    }

    if (request.SizeBytes <= 0)
    {
      errors["sizeBytes"] = ["SizeBytes must be greater than zero."];
    }
    else if (request.SizeBytes > _fileStorageOptions.MaxUploadBytes)
    {
      errors["sizeBytes"] = [$"SizeBytes must be less than or equal to {_fileStorageOptions.MaxUploadBytes} bytes."];
    }

    var normalizedContentType = NormalizeContentType(request.ContentType);
    if (contentError is not null)
    {
      errors["contentType"] = [contentError];
    }
    else if (string.IsNullOrWhiteSpace(normalizedContentType))
    {
      errors["contentType"] = ["ContentType is required."];
    }
    else
    {
      if (_allowedContentTypes.Count > 0 && !_allowedContentTypes.Contains(normalizedContentType))
      {
        errors["contentType"] = ["Este tipo de archivo no está permitido."];
      }

      if (!IsAllowedForPurpose(request, normalizedContentType))
      {
        errors["contentType"] = [AcceptedForPurposeMessage(request)];
      }
    }

    if (!Enum.IsDefined(request.Purpose))
    {
      errors["purpose"] = [MediaAssetPurposeParser.InvalidPurposeMessage];
    }
    else if (!CanUploadPurpose(roles, request.Purpose))
    {
      errors["purpose"] = ["The current role is not allowed to upload this media purpose."];
    }

    ThrowIfAnyErrors(errors);
  }

  private const string ImageFormatsText = "JPG, PNG, WebP, GIF o HEIC";

  private static bool AdmitsDocuments(MediaAssetUploadRequest request)
      => !request.ImagesOnly
          && PurposeContentTypes.TryGetValue(request.Purpose, out var allowed)
          && allowed.Contains(FileSignatures.Pdf, StringComparer.OrdinalIgnoreCase);

  private static bool IsAllowedForPurpose(MediaAssetUploadRequest request, string contentType)
  {
    if (!PurposeContentTypes.TryGetValue(request.Purpose, out var allowedForPurpose))
    {
      return true; // An undefined purpose is reported on its own field.
    }

    return allowedForPurpose.Contains(contentType, StringComparer.OrdinalIgnoreCase)
        && (!request.ImagesOnly || ImageContentTypes.Contains(contentType));
  }

  /// <summary>For a file whose signature is none of the accepted formats (HTML, SVG, text, executables...).</summary>
  private static string NotAcceptedMessage(MediaAssetUploadRequest request)
      => AdmitsDocuments(request)
          ? $"El archivo no es una imagen ni un PDF válido. Sube una foto ({ImageFormatsText}) o un PDF."
          : $"El archivo no es una imagen válida. Sube una foto en {ImageFormatsText}.";

  /// <summary>For a real file of a type the purpose does not take (a PDF as a profile photo, for instance).</summary>
  private static string AcceptedForPurposeMessage(MediaAssetUploadRequest request)
      => AdmitsDocuments(request)
          ? $"Aquí solo se aceptan imágenes ({ImageFormatsText}) o PDF."
          : $"Aquí solo se aceptan imágenes ({ImageFormatsText}).";

  /// <summary>Characters no file name may contain on any system, besides control characters.</summary>
  private static readonly char[] InvalidFileNameChars = ['<', '>', ':', '"', '|', '?', '*'];

  /// <summary>
  /// The name is only metadata (the storage key never uses it), but it is shown back and must be a plain file name on
  /// every system (BUG-098): Linux accepts "C:\x.png" or "a?b.png" as names. Keeps the last path segment and replaces
  /// characters that are invalid on Windows or control characters with "_".
  /// </summary>
  internal static string SanitizeFileName(string? fileName)
  {
    if (string.IsNullOrWhiteSpace(fileName))
    {
      return fileName ?? string.Empty;
    }

    var name = fileName.Trim();
    var lastSeparator = name.LastIndexOfAny(['/', '\\']);
    if (lastSeparator >= 0)
    {
      name = name[(lastSeparator + 1)..];
    }

    var sanitized = string.Create(name.Length, name, static (buffer, source) =>
    {
      for (var index = 0; index < source.Length; index++)
      {
        var character = source[index];
        buffer[index] = char.IsControl(character) || Array.IndexOf(InvalidFileNameChars, character) >= 0 ? '_' : character;
      }
    });

    return sanitized.Trim().TrimEnd('.');
  }

  private static bool CanUploadPurpose(IReadOnlyCollection<string> roles, MediaAssetPurpose purpose)
  {
    var roleSet = roles
        .Where(role => !string.IsNullOrWhiteSpace(role))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    if (roleSet.Contains(RoleNames.Admin))
    {
      return true;
    }

    if (roleSet.Contains(RoleNames.Staff))
    {
      return purpose is MediaAssetPurpose.StaffPhoto or MediaAssetPurpose.TipsQr;
    }

    if (roleSet.Contains(RoleNames.Customer))
    {
      return purpose is MediaAssetPurpose.CustomerReference;
    }

    return false;
  }

  private async Task EnsureActiveUserExistsAsync(Guid userId, CancellationToken cancellationToken)
  {
    var exists = await _dbContext.Users
        .AsNoTracking()
        .AnyAsync(user => user.Id == userId && user.IsActive, cancellationToken);

    if (!exists)
    {
      throw new KeyNotFoundException("The current user was not found.");
    }
  }

  private Task<bool> IsMediaAssetReferencedAsync(Guid mediaAssetId, CancellationToken cancellationToken)
      => MediaAssetReferences.IsReferencedAsync(_dbContext, mediaAssetId, cancellationToken);

  private static string BuildStorageKey(MediaAssetPurpose purpose, string fileName, string contentType, DateTime timestamp)
  {
    var extension = ResolveExtension(fileName, contentType);
    var purposeSegment = purpose.ToString().ToLowerInvariant();
    var dateSegment = timestamp.ToString("yyyyMMdd");
    var uniqueSegment = Guid.NewGuid().ToString("N");

    return $"media/{purposeSegment}/{dateSegment}/{uniqueSegment}{extension}";
  }

  private static string ResolveExtension(string fileName, string contentType)
  {
    // The content type is the one read from the file's signature, so it decides the stored extension.
    var byContentType = NormalizeContentType(contentType) switch
    {
      "image/jpeg" => ".jpg",
      "image/png" => ".png",
      "image/webp" => ".webp",
      "image/gif" => ".gif",
      "application/pdf" => ".pdf",
      _ => null
    };

    if (byContentType is not null)
    {
      return byContentType;
    }

    var extension = Path.GetExtension(fileName)?.Trim();
    if (!string.IsNullOrWhiteSpace(extension)
        && extension.StartsWith(".", StringComparison.Ordinal)
        && extension.Length <= 10
        && extension.Skip(1).All(char.IsLetterOrDigit))
    {
      return extension.ToLowerInvariant();
    }

    return NormalizeContentType(contentType) switch
    {
      "image/jpeg" => ".jpg",
      "image/png" => ".png",
      "image/webp" => ".webp",
      "image/gif" => ".gif",
      "application/pdf" => ".pdf",
      _ => ".bin"
    };
  }

  private static string NormalizeContentType(string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return string.Empty;
    }

    var sanitized = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
    return sanitized.Trim().ToLowerInvariant();
  }

  private static bool IsDangerousFileName(string fileName)
  {
    var trimmed = fileName.Trim();

    if (!string.Equals(trimmed, Path.GetFileName(trimmed), StringComparison.Ordinal))
    {
      return true;
    }

    if (trimmed.Contains("..", StringComparison.Ordinal))
    {
      return true;
    }

    if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
    {
      return true;
    }

    var extension = Path.GetExtension(trimmed);
    return !string.IsNullOrWhiteSpace(extension) && BlockedFileExtensions.Contains(extension);
  }

  private static string BuildFailureReason(Exception exception)
  {
    var message = string.IsNullOrWhiteSpace(exception.Message)
        ? "Storage upload failed."
        : $"Storage upload failed: {exception.Message}";

    return message.Length <= 500 ? message : message[..500];
  }

  private static MediaAssetView Map(MediaAsset mediaAsset)
      => new(
          mediaAsset.Id,
          mediaAsset.FileName,
          mediaAsset.ContentType,
          mediaAsset.SizeBytes,
          mediaAsset.StorageKey,
          mediaAsset.PublicUrl,
          mediaAsset.Purpose,
          mediaAsset.Status,
          mediaAsset.UploadedByUserId,
          mediaAsset.FailureReason,
          mediaAsset.CreatedAt,
          mediaAsset.UpdatedAt);

  private static void ValidateMediaAssetId(Guid mediaAssetId)
  {
    if (mediaAssetId == Guid.Empty)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["mediaAssetId"] = ["MediaAssetId is required."]
      });
    }
  }

  private static void ThrowIfAnyErrors(Dictionary<string, string[]> errors)
  {
    if (errors.Count > 0)
    {
      throw new ValidationProblemException(errors);
    }
  }
}
