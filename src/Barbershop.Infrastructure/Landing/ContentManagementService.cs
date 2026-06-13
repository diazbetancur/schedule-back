using System.Text.RegularExpressions;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Landing;
using Barbershop.Application.PublicContent;
using Barbershop.Domain.Landing;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Landing;

internal sealed partial class ContentManagementService : IPublicContentService, IAdminContentService
{
  private const string DefaultHeroTitle = "Reserva tu proximo turno";
  private const string DefaultAppName = "Barbershop";
  private const string DefaultPrimaryColor = "#111111";
  private const string DefaultSecondaryColor = "#C59D5F";

  private readonly AppDbContext _dbContext;
  private readonly TimeProvider _timeProvider;

  public ContentManagementService(AppDbContext dbContext, TimeProvider timeProvider)
  {
    _dbContext = dbContext;
    _timeProvider = timeProvider;
  }

  public async Task<LandingContentResponse> GetPublicLandingAsync(CancellationToken cancellationToken = default)
  {
    var landingContent = await _dbContext.LandingContents
        .AsNoTracking()
        .OrderByDescending(content => content.UpdatedAt ?? DateTime.MinValue)
        .FirstOrDefaultAsync(cancellationToken);

    return landingContent is null
        ? CreateDefaultLandingContentResponse()
        : MapLandingContent(landingContent);
  }

  public async Task<IReadOnlyList<BannerResponse>> GetPublicBannersAsync(CancellationToken cancellationToken = default)
  {
    var now = _timeProvider.GetUtcNow().UtcDateTime;
    var banners = await _dbContext.Banners
        .AsNoTracking()
        .Where(banner => banner.IsActive)
        .Where(banner => !banner.StartsAt.HasValue || banner.StartsAt.Value <= now)
        .Where(banner => !banner.EndsAt.HasValue || banner.EndsAt.Value > now)
        .OrderBy(banner => banner.SortOrder)
        .ThenBy(banner => banner.CreatedAt)
        .ToListAsync(cancellationToken);

    var mediaUrls = await LoadMediaUrlsAsync(banners.Select(banner => banner.ImageMediaAssetId), cancellationToken);

    return banners
        .Select(banner => MapBanner(banner, mediaUrls))
        .ToList();
  }

  public async Task<BrandingSettingsResponse> GetPublicBrandingAsync(CancellationToken cancellationToken = default)
  {
    var branding = await _dbContext.AppBrandingSettings
        .AsNoTracking()
        .OrderByDescending(settings => settings.UpdatedAt ?? DateTime.MinValue)
        .FirstOrDefaultAsync(cancellationToken);

    if (branding is null)
    {
      return CreateDefaultBrandingSettingsResponse();
    }

    var mediaUrls = await LoadMediaUrlsAsync([branding.LogoMediaAssetId, branding.AppIconMediaAssetId], cancellationToken);
    return MapBranding(branding, mediaUrls);
  }

  public async Task<LandingContentResponse> GetLandingAsync(CancellationToken cancellationToken = default)
  {
    var landingContent = await _dbContext.LandingContents
        .AsNoTracking()
        .OrderByDescending(content => content.UpdatedAt ?? DateTime.MinValue)
        .FirstOrDefaultAsync(cancellationToken);

    return landingContent is null
        ? CreateDefaultLandingContentResponse()
        : MapLandingContent(landingContent);
  }

  public async Task<LandingContentResponse> UpsertLandingAsync(UpsertLandingContentRequest request, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(request);

    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
    ValidateRequiredText(request.HeroTitle, "heroTitle", 2, 200, errors, "HeroTitle is required and must be between 2 and 200 characters.");
    ThrowIfAnyValidationErrors(errors);

    var now = _timeProvider.GetUtcNow().UtcDateTime;

    var landingContent = await _dbContext.LandingContents
        .OrderByDescending(content => content.UpdatedAt ?? DateTime.MinValue)
        .FirstOrDefaultAsync(cancellationToken);

    if (landingContent is null)
    {
      landingContent = new LandingContent(request.HeroTitle, now);
      _dbContext.LandingContents.Add(landingContent);
    }

    landingContent.Update(
        request.HeroTitle,
        request.HeroSubtitle,
        request.AboutTitle,
        request.AboutText,
        request.ContactPhone,
        request.MapsUrl,
        request.Address,
        now);

    await _dbContext.SaveChangesAsync(cancellationToken);

    return MapLandingContent(landingContent);
  }

  public async Task<IReadOnlyList<BannerResponse>> GetBannersAsync(CancellationToken cancellationToken = default)
  {
    var banners = await _dbContext.Banners
        .AsNoTracking()
        .OrderBy(banner => banner.SortOrder)
        .ThenBy(banner => banner.CreatedAt)
        .ToListAsync(cancellationToken);

    var mediaUrls = await LoadMediaUrlsAsync(banners.Select(banner => banner.ImageMediaAssetId), cancellationToken);

    return banners
        .Select(banner => MapBanner(banner, mediaUrls))
        .ToList();
  }

  public async Task<BannerResponse> GetBannerByIdAsync(Guid bannerId, CancellationToken cancellationToken = default)
  {
    if (bannerId == Guid.Empty)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["bannerId"] = ["BannerId is required."]
      });
    }

    var banner = await _dbContext.Banners
        .AsNoTracking()
        .SingleOrDefaultAsync(candidate => candidate.Id == bannerId, cancellationToken)
        ?? throw new KeyNotFoundException("The banner was not found.");

    var mediaUrls = await LoadMediaUrlsAsync([banner.ImageMediaAssetId], cancellationToken);
    return MapBanner(banner, mediaUrls);
  }

  public async Task<BannerResponse> CreateBannerAsync(CreateBannerRequest request, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(request);

    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
    ValidateBannerRequest(request.Title, request.Subtitle, request.LinkUrl, request.StartsAtUtc, request.EndsAtUtc, "imageMediaAssetId", errors);
    await ValidateMediaAssetReferenceAsync(request.ImageMediaAssetId, "imageMediaAssetId", errors, cancellationToken);
    ThrowIfAnyValidationErrors(errors);

    var now = _timeProvider.GetUtcNow().UtcDateTime;

    var banner = new Banner(
        request.Title,
        request.SortOrder,
        now,
        request.Subtitle,
        request.ImageMediaAssetId,
        request.LinkUrl);

    banner.Update(
        request.Title,
        request.Subtitle,
        request.ImageMediaAssetId,
        request.LinkUrl,
        request.SortOrder,
        request.IsActive,
        request.StartsAtUtc,
        request.EndsAtUtc,
        now);

    _dbContext.Banners.Add(banner);
    await _dbContext.SaveChangesAsync(cancellationToken);

    var mediaUrls = await LoadMediaUrlsAsync([banner.ImageMediaAssetId], cancellationToken);
    return MapBanner(banner, mediaUrls);
  }

  public async Task<BannerResponse> UpdateBannerAsync(Guid bannerId, UpdateBannerRequest request, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(request);

    if (bannerId == Guid.Empty)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["bannerId"] = ["BannerId is required."]
      });
    }

    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
    ValidateBannerRequest(request.Title, request.Subtitle, request.LinkUrl, request.StartsAtUtc, request.EndsAtUtc, "imageMediaAssetId", errors);
    await ValidateMediaAssetReferenceAsync(request.ImageMediaAssetId, "imageMediaAssetId", errors, cancellationToken);
    ThrowIfAnyValidationErrors(errors);

    var banner = await _dbContext.Banners
        .SingleOrDefaultAsync(candidate => candidate.Id == bannerId, cancellationToken)
        ?? throw new KeyNotFoundException("The banner was not found.");

    var now = _timeProvider.GetUtcNow().UtcDateTime;

    banner.Update(
        request.Title,
        request.Subtitle,
        request.ImageMediaAssetId,
        request.LinkUrl,
        request.SortOrder,
        request.IsActive,
        request.StartsAtUtc,
        request.EndsAtUtc,
        now);

    await _dbContext.SaveChangesAsync(cancellationToken);

    var mediaUrls = await LoadMediaUrlsAsync([banner.ImageMediaAssetId], cancellationToken);
    return MapBanner(banner, mediaUrls);
  }

  public async Task DeleteBannerAsync(Guid bannerId, CancellationToken cancellationToken = default)
  {
    if (bannerId == Guid.Empty)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["bannerId"] = ["BannerId is required."]
      });
    }

    var banner = await _dbContext.Banners
        .SingleOrDefaultAsync(candidate => candidate.Id == bannerId, cancellationToken)
        ?? throw new KeyNotFoundException("The banner was not found.");

    _dbContext.Banners.Remove(banner);
    await _dbContext.SaveChangesAsync(cancellationToken);
  }

  public async Task<BrandingSettingsResponse> GetBrandingAsync(CancellationToken cancellationToken = default)
  {
    var branding = await _dbContext.AppBrandingSettings
        .AsNoTracking()
        .OrderByDescending(settings => settings.UpdatedAt ?? DateTime.MinValue)
        .FirstOrDefaultAsync(cancellationToken);

    if (branding is null)
    {
      return CreateDefaultBrandingSettingsResponse();
    }

    var mediaUrls = await LoadMediaUrlsAsync([branding.LogoMediaAssetId, branding.AppIconMediaAssetId], cancellationToken);
    return MapBranding(branding, mediaUrls);
  }

  public async Task<BrandingSettingsResponse> UpsertBrandingAsync(UpsertBrandingSettingsRequest request, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(request);

    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
    ValidateRequiredText(request.AppName, "appName", 2, 120, errors, "AppName is required and must be between 2 and 120 characters.");
    ValidateHexColor(request.PrimaryColor, "primaryColor", errors);
    ValidateHexColor(request.SecondaryColor, "secondaryColor", errors);

    await ValidateMediaAssetReferenceAsync(request.LogoMediaAssetId, "logoMediaAssetId", errors, cancellationToken);
    await ValidateMediaAssetReferenceAsync(request.AppIconMediaAssetId, "appIconMediaAssetId", errors, cancellationToken);
    ThrowIfAnyValidationErrors(errors);

    var now = _timeProvider.GetUtcNow().UtcDateTime;
    var branding = await _dbContext.AppBrandingSettings
        .OrderByDescending(settings => settings.UpdatedAt ?? DateTime.MinValue)
        .FirstOrDefaultAsync(cancellationToken);

    if (branding is null)
    {
      branding = new AppBrandingSettings(request.AppName, request.PrimaryColor, request.SecondaryColor, now);
      _dbContext.AppBrandingSettings.Add(branding);
    }

    branding.Update(
        request.AppName,
        request.PrimaryColor,
        request.SecondaryColor,
        request.LogoMediaAssetId,
        request.AppIconMediaAssetId,
        now);

    await _dbContext.SaveChangesAsync(cancellationToken);

    var mediaUrls = await LoadMediaUrlsAsync([branding.LogoMediaAssetId, branding.AppIconMediaAssetId], cancellationToken);
    return MapBranding(branding, mediaUrls);
  }

  private async Task<Dictionary<Guid, string?>> LoadMediaUrlsAsync(IEnumerable<Guid?> mediaIds, CancellationToken cancellationToken)
  {
    var ids = mediaIds
        .Where(id => id.HasValue && id.Value != Guid.Empty)
        .Select(id => id!.Value)
        .Distinct()
        .ToArray();

    if (ids.Length == 0)
    {
      return [];
    }

    return await _dbContext.MediaAssets
        .AsNoTracking()
        .Where(media => ids.Contains(media.Id))
        .ToDictionaryAsync(media => media.Id, media => media.PublicUrl, cancellationToken);
  }

  private async Task ValidateMediaAssetReferenceAsync(
      Guid? mediaAssetId,
      string fieldName,
      Dictionary<string, string[]> errors,
      CancellationToken cancellationToken)
  {
    if (!mediaAssetId.HasValue)
    {
      return;
    }

    if (mediaAssetId == Guid.Empty)
    {
      errors[fieldName] = ["The media asset identifier must be a valid GUID."];
      return;
    }

    var exists = await _dbContext.MediaAssets
        .AsNoTracking()
        .AnyAsync(media => media.Id == mediaAssetId.Value, cancellationToken);

    if (!exists)
    {
      errors[fieldName] = ["The referenced media asset was not found."];
    }
  }

  private static void ValidateBannerRequest(
      string title,
      string? subtitle,
      string? linkUrl,
      DateTime? startsAtUtc,
      DateTime? endsAtUtc,
      string imageFieldName,
      Dictionary<string, string[]> errors)
  {
    ValidateRequiredText(title, "title", 2, 160, errors, "Title is required and must be between 2 and 160 characters.");

    if (!string.IsNullOrWhiteSpace(subtitle) && subtitle.Trim().Length > 300)
    {
      errors["subtitle"] = ["Subtitle must be 300 characters or fewer."];
    }

    if (!string.IsNullOrWhiteSpace(linkUrl) && !Uri.TryCreate(linkUrl.Trim(), UriKind.Absolute, out _))
    {
      errors["linkUrl"] = ["LinkUrl must be a valid absolute URL."];
    }

    if (startsAtUtc.HasValue && endsAtUtc.HasValue && endsAtUtc.Value <= startsAtUtc.Value)
    {
      errors["endsAtUtc"] = ["EndsAtUtc must be after StartsAtUtc when both values are provided."];
    }

    _ = imageFieldName;
  }

  private static void ValidateHexColor(string? value, string fieldName, Dictionary<string, string[]> errors)
  {
    if (string.IsNullOrWhiteSpace(value) || !HexColorRegex().IsMatch(value.Trim()))
    {
      errors[fieldName] = ["The value must be a valid hex color in the format #RRGGBB."];
    }
  }

  private static void ValidateRequiredText(
      string? value,
      string fieldName,
      int minLength,
      int maxLength,
      Dictionary<string, string[]> errors,
      string message)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      errors[fieldName] = [message];
      return;
    }

    var trimmed = value.Trim();
    if (trimmed.Length < minLength || trimmed.Length > maxLength)
    {
      errors[fieldName] = [message];
    }
  }

  private static void ThrowIfAnyValidationErrors(Dictionary<string, string[]> errors)
  {
    if (errors.Count > 0)
    {
      throw new ValidationProblemException(errors);
    }
  }

  private static LandingContentResponse MapLandingContent(LandingContent content)
      => new(
          content.HeroTitle,
          content.HeroSubtitle,
          content.AboutTitle,
          content.AboutText,
          content.ContactPhone,
          content.MapsUrl,
          content.Address,
          content.UpdatedAt);

  private static BannerResponse MapBanner(Banner banner, IReadOnlyDictionary<Guid, string?> mediaUrls)
  {
    var imageUrl = ResolveMediaUrl(banner.ImageMediaAssetId, mediaUrls);

    return new BannerResponse(
        banner.Id,
        banner.Title,
        banner.Subtitle,
        banner.ImageMediaAssetId,
        imageUrl,
        banner.LinkUrl,
        banner.SortOrder,
        banner.IsActive,
        banner.StartsAt,
        banner.EndsAt,
        banner.CreatedAt,
        banner.UpdatedAt);
  }

  private static BrandingSettingsResponse MapBranding(AppBrandingSettings branding, IReadOnlyDictionary<Guid, string?> mediaUrls)
      => new(
          branding.AppName,
          branding.PrimaryColor,
          branding.SecondaryColor,
          branding.LogoMediaAssetId,
          branding.AppIconMediaAssetId,
          ResolveMediaUrl(branding.LogoMediaAssetId, mediaUrls),
          ResolveMediaUrl(branding.AppIconMediaAssetId, mediaUrls),
          branding.UpdatedAt);

  private static string? ResolveMediaUrl(Guid? mediaAssetId, IReadOnlyDictionary<Guid, string?> mediaUrls)
  {
    if (!mediaAssetId.HasValue)
    {
      return null;
    }

    return mediaUrls.TryGetValue(mediaAssetId.Value, out var mediaUrl)
        ? mediaUrl
        : null;
  }

  private static LandingContentResponse CreateDefaultLandingContentResponse()
      => new(
          DefaultHeroTitle,
          null,
          null,
          null,
          null,
          null,
          null,
          null);

  private static BrandingSettingsResponse CreateDefaultBrandingSettingsResponse()
      => new(
          DefaultAppName,
          DefaultPrimaryColor,
          DefaultSecondaryColor,
          null,
          null,
          null,
          null,
          null);

  // ── Business Schedule ─────────────────────────────────────────────────

  public Task<BusinessScheduleResponse> GetPublicBusinessScheduleAsync(CancellationToken cancellationToken = default)
      => GetScheduleInternalAsync(cancellationToken);

  public Task<BusinessScheduleResponse> GetBusinessScheduleAsync(CancellationToken cancellationToken = default)
      => GetScheduleInternalAsync(cancellationToken);

  public async Task<BusinessScheduleResponse> UpsertBusinessScheduleAsync(
      UpsertBusinessScheduleRequest request,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(request);

    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    if (request.Days is null || request.Days.Count != 7)
    {
      errors["days"] = ["Se deben proveer exactamente 7 entradas (una por dia de la semana)."];
      ThrowIfAnyValidationErrors(errors);
    }

    var orderedDayNumbers = request.Days!.Select(d => d.DayOfWeek).OrderBy(d => d).ToList();
    if (!orderedDayNumbers.SequenceEqual(Enumerable.Range(0, 7)))
    {
      errors["days"] = ["Los dias deben ser exactamente 0-6 (lunes a domingo) sin duplicados."];
      ThrowIfAnyValidationErrors(errors);
    }

    foreach (var day in request.Days)
    {
      var key = $"days[{day.DayOfWeek}]";

      if (!day.IsOpen)
        continue;

      if (string.IsNullOrWhiteSpace(day.OpenTime) || string.IsNullOrWhiteSpace(day.CloseTime))
      {
        errors[key] = ["El dia esta marcado como abierto pero le falta hora de apertura o cierre."];
        continue;
      }

      if (!TimeOnly.TryParseExact(day.OpenTime.Trim(), "HH:mm", out var openTime) ||
          !TimeOnly.TryParseExact(day.CloseTime.Trim(), "HH:mm", out var closeTime))
      {
        errors[key] = ["El formato de hora es invalido. Use HH:mm (ej: 09:00)."];
        continue;
      }

      if (openTime >= closeTime)
      {
        errors[key] = ["La hora de apertura debe ser anterior a la hora de cierre."];
      }
    }

    ThrowIfAnyValidationErrors(errors);

    var existing = await _dbContext.BusinessScheduleDays.ToListAsync(cancellationToken);
    _dbContext.BusinessScheduleDays.RemoveRange(existing);

    var newDays = request.Days.Select(d =>
    {
      TimeOnly? openTime = d.IsOpen && !string.IsNullOrWhiteSpace(d.OpenTime)
          ? TimeOnly.ParseExact(d.OpenTime.Trim(), "HH:mm") : null;
      TimeOnly? closeTime = d.IsOpen && !string.IsNullOrWhiteSpace(d.CloseTime)
          ? TimeOnly.ParseExact(d.CloseTime.Trim(), "HH:mm") : null;

      return new BusinessScheduleDay(d.DayOfWeek, d.IsOpen, openTime, closeTime);
    }).ToList();

    _dbContext.BusinessScheduleDays.AddRange(newDays);
    await _dbContext.SaveChangesAsync(cancellationToken);

    return MapSchedule(newDays);
  }

  private async Task<BusinessScheduleResponse> GetScheduleInternalAsync(CancellationToken cancellationToken)
  {
    var days = await _dbContext.BusinessScheduleDays
        .AsNoTracking()
        .OrderBy(d => d.DayOfWeek)
        .ToListAsync(cancellationToken);

    return days.Count == 0 ? CreateDefaultBusinessSchedule() : MapSchedule(days);
  }

  private static BusinessScheduleResponse MapSchedule(IEnumerable<BusinessScheduleDay> days)
      => new(days
          .OrderBy(d => d.DayOfWeek)
          .Select(d => new BusinessScheduleDayResponse(
              d.DayOfWeek,
              d.IsOpen,
              d.OpenTime?.ToString("HH:mm"),
              d.CloseTime?.ToString("HH:mm")))
          .ToList());

  private static BusinessScheduleResponse CreateDefaultBusinessSchedule()
  {
    // Mon(0)–Sat(5) 09:00–19:00 open, Sun(6) closed
    var days = Enumerable.Range(0, 7)
        .Select(i => new BusinessScheduleDayResponse(i, i < 6, i < 6 ? "09:00" : null, i < 6 ? "19:00" : null))
        .ToList();
    return new BusinessScheduleResponse(days);
  }

  [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
  private static partial Regex HexColorRegex();
}
