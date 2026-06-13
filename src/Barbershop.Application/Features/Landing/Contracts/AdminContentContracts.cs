namespace Barbershop.Application.Landing;

public sealed record UpsertLandingContentRequest(
    string HeroTitle,
    string? HeroSubtitle,
    string? AboutTitle,
    string? AboutText,
    string? ContactPhone,
    string? MapsUrl,
    string? Address);

public sealed record UpsertBrandingSettingsRequest(
    string AppName,
    string PrimaryColor,
    string SecondaryColor,
    Guid? LogoMediaAssetId,
    Guid? AppIconMediaAssetId);

public sealed record CreateBannerRequest(
    string Title,
    string? Subtitle,
    Guid? ImageMediaAssetId,
    string? LinkUrl,
    int SortOrder,
    bool IsActive,
    DateTime? StartsAtUtc,
    DateTime? EndsAtUtc);

public sealed record UpdateBannerRequest(
    string Title,
    string? Subtitle,
    Guid? ImageMediaAssetId,
    string? LinkUrl,
    int SortOrder,
    bool IsActive,
    DateTime? StartsAtUtc,
    DateTime? EndsAtUtc);

public sealed record UpsertBusinessScheduleRequest(
    IReadOnlyList<BusinessScheduleDayRequest> Days);

/// <param name="DayOfWeek">0 = Monday … 6 = Sunday</param>
/// <param name="OpenTime">HH:mm format, null when IsOpen is false</param>
/// <param name="CloseTime">HH:mm format, null when IsOpen is false</param>
public sealed record BusinessScheduleDayRequest(
    int DayOfWeek,
    bool IsOpen,
    string? OpenTime,
    string? CloseTime);
