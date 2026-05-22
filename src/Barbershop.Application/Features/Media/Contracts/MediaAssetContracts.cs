using Barbershop.Domain.Media;

namespace Barbershop.Application.Media;

public sealed record MediaAssetUploadRequest(
    string FileName,
    string ContentType,
    long SizeBytes,
    MediaAssetPurpose Purpose,
    Stream Content);

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
