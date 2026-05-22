namespace Barbershop.Application.Staff.SelfService;

public sealed record StaffProfileUpdateRequest(
    string DisplayName,
    string? Bio,
    string? PhoneNumber,
    int? DefaultAppointmentDurationMinutes,
    Guid? PhotoMediaAssetId,
    Guid? TipsQrMediaAssetId,
    string? InstagramUrl,
    string? FacebookUrl,
    string? TikTokUrl,
    string? YoutubeUrl,
    string? XUrl);
