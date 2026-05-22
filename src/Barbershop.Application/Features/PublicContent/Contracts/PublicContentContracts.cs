namespace Barbershop.Application.PublicContent;

public sealed record LandingContentResponse(
    string HeroTitle,
    string? HeroSubtitle,
    string? AboutTitle,
    string? AboutText,
    string? ContactPhone,
    string? MapsUrl,
    string? Address,
    DateTime? UpdatedAtUtc);

public sealed record BrandingSettingsResponse(
    string AppName,
    string PrimaryColor,
    string SecondaryColor,
    Guid? LogoMediaAssetId,
    Guid? AppIconMediaAssetId,
    string? LogoUrl,
    string? AppIconUrl,
    DateTime? UpdatedAtUtc);

public sealed record BannerResponse(
    Guid Id,
    string Title,
    string? Subtitle,
    Guid? ImageMediaAssetId,
    string? ImageUrl,
    string? LinkUrl,
    int SortOrder,
    bool IsActive,
    DateTime? StartsAtUtc,
    DateTime? EndsAtUtc,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);

public sealed record PublicStaffServiceResponse(
    Guid Id,
    string Name,
    string? Description,
    int DurationMinutes,
    decimal? Price,
    bool IsActive);

public sealed record PublicStaffListItemResponse(
    Guid StaffProfileId,
    string DisplayName,
    string? Bio,
    string? PhoneNumber,
    Guid? PhotoMediaAssetId,
    string? PhotoUrl,
    int DefaultAppointmentDurationMinutes,
    decimal AverageRating,
    int ReviewCount,
    IReadOnlyList<PublicStaffServiceResponse> Services);

public sealed record PublicStaffProfileResponse(
    Guid StaffProfileId,
    string DisplayName,
    string? Bio,
    string? PhoneNumber,
    Guid? PhotoMediaAssetId,
    string? PhotoUrl,
    Guid? TipsQrMediaAssetId,
    string? TipsQrUrl,
    int DefaultAppointmentDurationMinutes,
    decimal AverageRating,
    int ReviewCount,
    IReadOnlyList<PublicStaffServiceResponse> Services,
    string? InstagramUrl,
    string? FacebookUrl,
    string? TikTokUrl,
    string? YoutubeUrl,
    string? XUrl);
