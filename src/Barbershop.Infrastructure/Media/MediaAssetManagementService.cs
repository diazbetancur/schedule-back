using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Media;
using Barbershop.Application.Storage;
using Barbershop.Domain.Media;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Configuration;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
        [MediaAssetPurpose.Other] = ImageContentTypes.Concat(DocumentContentTypes).ToArray()
      };

  private readonly AppDbContext _dbContext;
  private readonly IFileStorageService _fileStorageService;
  private readonly TimeProvider _timeProvider;
  private readonly FileStorageOptions _fileStorageOptions;
  private readonly HashSet<string> _allowedContentTypes;

  public MediaAssetManagementService(
      AppDbContext dbContext,
      IFileStorageService fileStorageService,
      TimeProvider timeProvider,
      IOptions<FileStorageOptions> fileStorageOptions)
  {
    _dbContext = dbContext;
    _fileStorageService = fileStorageService;
    _timeProvider = timeProvider;
    _fileStorageOptions = fileStorageOptions.Value;
    _allowedContentTypes = _fileStorageOptions.AllowedContentTypes
        .Select(NormalizeContentType)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
  }

  public async Task<MediaAssetView> UploadAsync(
      Guid currentUserId,
      IReadOnlyCollection<string> roles,
      MediaAssetUploadRequest request,
      CancellationToken cancellationToken = default)
  {
    ValidateUploadRequest(currentUserId, roles, request);
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
      throw new ServiceUnavailableException("The media storage service is temporarily unavailable.");
    }
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

  private void ValidateUploadRequest(Guid currentUserId, IReadOnlyCollection<string> roles, MediaAssetUploadRequest request)
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
    if (string.IsNullOrWhiteSpace(normalizedContentType))
    {
      errors["contentType"] = ["ContentType is required."];
    }
    else
    {
      if (_allowedContentTypes.Count > 0 && !_allowedContentTypes.Contains(normalizedContentType))
      {
        errors["contentType"] = ["ContentType is not allowed for uploads."];
      }

      if (PurposeContentTypes.TryGetValue(request.Purpose, out var allowedForPurpose)
          && !allowedForPurpose.Contains(normalizedContentType, StringComparer.OrdinalIgnoreCase))
      {
        errors["contentType"] = ["ContentType is invalid for the selected purpose."];
      }
    }

    if (!CanUploadPurpose(roles, request.Purpose))
    {
      errors["purpose"] = ["The current role is not allowed to upload this media purpose."];
    }

    ThrowIfAnyErrors(errors);
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

  private async Task<bool> IsMediaAssetReferencedAsync(Guid mediaAssetId, CancellationToken cancellationToken)
  {
    var referencedByUser = await _dbContext.Users
        .AsNoTracking()
        .AnyAsync(user => user.ProfilePhotoMediaAssetId == mediaAssetId, cancellationToken);

    if (referencedByUser)
    {
      return true;
    }

    var referencedByStaff = await _dbContext.StaffProfiles
        .AsNoTracking()
        .AnyAsync(staff => staff.PhotoMediaAssetId == mediaAssetId || staff.TipsQrMediaAssetId == mediaAssetId, cancellationToken);

    if (referencedByStaff)
    {
      return true;
    }

    var referencedByBanner = await _dbContext.Banners
        .AsNoTracking()
        .AnyAsync(banner => banner.ImageMediaAssetId == mediaAssetId, cancellationToken);

    if (referencedByBanner)
    {
      return true;
    }

    return await _dbContext.AppBrandingSettings
        .AsNoTracking()
        .AnyAsync(branding => branding.LogoMediaAssetId == mediaAssetId || branding.AppIconMediaAssetId == mediaAssetId, cancellationToken);
  }

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
