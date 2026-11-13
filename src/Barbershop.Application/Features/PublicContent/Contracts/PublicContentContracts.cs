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

public sealed record BusinessScheduleResponse(
    IReadOnlyList<BusinessScheduleDayResponse> Days);

/// <param name="DayOfWeek">0 = Monday … 6 = Sunday</param>
/// <param name="OpenTime">HH:mm format, null when IsOpen is false</param>
/// <param name="CloseTime">HH:mm format, null when IsOpen is false</param>
public sealed record BusinessScheduleDayResponse(
    int DayOfWeek,
    bool IsOpen,
    string? OpenTime,
    string? CloseTime);

/// <summary>
/// A general service the professional offers (ADR-0016 B2), with how long it takes them. Never a price: booking
/// shows only the service and its duration (F5).
/// </summary>
public sealed record PublicStaffServiceResponse(
    Guid GeneralServiceId,
    string Name,
    string? Description,
    Guid CategoryId,
    string CategoryName,
    int DurationMinutes);

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

public sealed record TickerItemResponse(
    Guid Id,
    string Text,
    int SortOrder,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);

// ── Public contracts (BUG-101): only what the public pages show. No file ids, no active flags (the public lists
// only carry active items, already ordered), no sort order and no edit timestamps. ──

public sealed record PublicLandingContentResponse(
    string HeroTitle,
    string? HeroSubtitle,
    string? AboutTitle,
    string? AboutText,
    string? ContactPhone,
    string? MapsUrl,
    string? Address)
{
  public static PublicLandingContentResponse From(LandingContentResponse content)
      => new(content.HeroTitle, content.HeroSubtitle, content.AboutTitle, content.AboutText, content.ContactPhone,
          content.MapsUrl, content.Address);
}

public sealed record PublicBrandingResponse(
    string AppName,
    string PrimaryColor,
    string SecondaryColor,
    string? LogoUrl,
    string? AppIconUrl)
{
  public static PublicBrandingResponse From(BrandingSettingsResponse branding)
      => new(branding.AppName, branding.PrimaryColor, branding.SecondaryColor, branding.LogoUrl, branding.AppIconUrl);
}

/// <param name="Id">The banner's own id (a key for the list), never a file id.</param>
public sealed record PublicBannerResponse(
    Guid Id,
    string Title,
    string? Subtitle,
    string? ImageUrl,
    string? LinkUrl)
{
  public static PublicBannerResponse From(BannerResponse banner)
      => new(banner.Id, banner.Title, banner.Subtitle, banner.ImageUrl, banner.LinkUrl);
}

public sealed record PublicTickerItemResponse(Guid Id, string Text)
{
  public static PublicTickerItemResponse From(TickerItemResponse item) => new(item.Id, item.Text);
}
