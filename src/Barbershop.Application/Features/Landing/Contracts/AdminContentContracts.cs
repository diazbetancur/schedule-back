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
