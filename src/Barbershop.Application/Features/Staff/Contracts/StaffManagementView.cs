namespace Barbershop.Application.Staff;

public sealed record StaffManagementView(
    Guid StaffProfileId,
    Guid UserId,
    string FullName,
    string Email,
    string? PhoneNumber,
    string DisplayName,
    string? Bio,
    int DefaultAppointmentDurationMinutes,
    Guid? PhotoMediaAssetId,
    string? PhotoUrl,
    Guid? TipsQrMediaAssetId,
    string? TipsQrUrl,
    string? InstagramUrl,
    string? FacebookUrl,
    string? TikTokUrl,
    string? YoutubeUrl,
    string? XUrl,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
