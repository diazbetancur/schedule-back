using Barbershop.Domain.Common;

namespace Barbershop.Domain.Media;

public sealed class MediaAsset
{
  private MediaAsset()
  {
  }

  public MediaAsset(
      string fileName,
      string contentType,
      long sizeBytes,
      string storageKey,
      MediaAssetPurpose purpose,
      Guid uploadedByUserId,
      DateTime createdAt,
      string? publicUrl = null)
  {
    FileName = DomainValidation.Required(fileName, nameof(fileName), 260, 1);
    ContentType = DomainValidation.Required(contentType, nameof(contentType), 150, 3);
    SizeBytes = DomainValidation.EnsurePositive(sizeBytes, nameof(sizeBytes));
    StorageKey = DomainValidation.Required(storageKey, nameof(storageKey), 512, 3);
    PublicUrl = DomainValidation.OptionalAbsoluteUriString(publicUrl, nameof(publicUrl));
    Purpose = purpose;
    UploadedByUserId = EnsureUserId(uploadedByUserId, nameof(uploadedByUserId));
    Status = MediaAssetStatus.Pending;
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public string FileName { get; private set; } = string.Empty;
  public string ContentType { get; private set; } = string.Empty;
  public long SizeBytes { get; private set; }
  public string StorageKey { get; private set; } = string.Empty;
  public string? PublicUrl { get; private set; }
  public MediaAssetPurpose Purpose { get; private set; }
  public MediaAssetStatus Status { get; private set; } = MediaAssetStatus.Pending;
  public Guid UploadedByUserId { get; private set; }
  public string? FailureReason { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }

  public void MarkReady(string? publicUrl, DateTime updatedAt)
  {
    EnsureNotArchived(nameof(updatedAt));

    PublicUrl = DomainValidation.OptionalAbsoluteUriString(publicUrl, nameof(publicUrl));
    FailureReason = null;
    Status = MediaAssetStatus.Ready;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public void MarkFailed(string failureReason, DateTime updatedAt)
  {
    EnsureNotArchived(nameof(updatedAt));

    FailureReason = DomainValidation.Required(failureReason, nameof(failureReason), 500, 3);
    Status = MediaAssetStatus.Failed;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public void Archive(DateTime updatedAt)
  {
    if (Status is not MediaAssetStatus.Ready)
    {
      throw new InvalidOperationException("Only ready media assets can be archived.");
    }

    Status = MediaAssetStatus.Archived;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  private void EnsureNotArchived(string paramName)
  {
    if (Status is MediaAssetStatus.Archived)
    {
      throw new InvalidOperationException($"The media asset is archived and cannot be modified ({paramName}).");
    }
  }

  private static Guid EnsureUserId(Guid value, string paramName)
  {
    if (value == Guid.Empty)
    {
      throw new ArgumentOutOfRangeException(paramName, $"{paramName} must be a valid identifier.");
    }

    return value;
  }
}

public enum MediaAssetPurpose
{
  Logo = 1,
  AppIcon = 2,
  Banner = 3,
  StaffPhoto = 4,
  TipsQr = 5,
  CustomerReference = 6,
  Other = 7
}

public enum MediaAssetStatus
{
  Pending = 1,
  Ready = 2,
  Archived = 3,
  Failed = 4
}