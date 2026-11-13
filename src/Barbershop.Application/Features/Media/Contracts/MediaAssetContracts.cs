using Barbershop.Domain.Media;

namespace Barbershop.Application.Media;

/// <param name="ImagesOnly">
/// Narrows the purpose to images: a profile photo travels as CustomerReference, which also admits PDF (BUG-058).
/// </param>
public sealed record MediaAssetUploadRequest(
    string FileName,
    string ContentType,
    long SizeBytes,
    MediaAssetPurpose Purpose,
    Stream Content,
    bool ImagesOnly = false);

public sealed record MediaAssetView(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string StorageKey,
    string? PublicUrl,
    MediaAssetPurpose Purpose,
    MediaAssetStatus Status,
    Guid UploadedByUserId,
    string? FailureReason,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);
