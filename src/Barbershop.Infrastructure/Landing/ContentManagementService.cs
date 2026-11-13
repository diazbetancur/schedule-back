using System.Text.RegularExpressions;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Landing;
using Barbershop.Application.PublicContent;
using Barbershop.Domain.Common;
using Barbershop.Domain.Landing;
using Barbershop.Domain.Media;
using Barbershop.Infrastructure.Media;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Landing;

internal sealed partial class ContentManagementService : IPublicContentService, IAdminContentService
{
  private const string DefaultHeroTitle = "Reserva tu próximo turno";
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

    var mediaUrls = await LoadPublishableMediaUrlsAsync(
        banners.Select(banner => (banner.ImageMediaAssetId, MediaAssetPurpose.Banner)), cancellationToken);

    return banners
        .Select(banner => MapBanner(banner, mediaUrls, hideUnpublishable: true))
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

    var mediaUrls = await LoadPublishableMediaUrlsAsync(
        [(branding.LogoMediaAssetId, MediaAssetPurpose.Logo), (branding.AppIconMediaAssetId, MediaAssetPurpose.AppIcon)],
        cancellationToken);
    return MapBranding(branding, mediaUrls, hideUnpublishable: true);
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
    ValidateRequiredText(request.HeroTitle, "heroTitle", 2, 200, errors, "El título principal es obligatorio y debe tener entre 2 y 200 caracteres.");
    ValidateMapsUrl(request.MapsUrl, errors);
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
        ["bannerId"] = ["Falta el banner."]
      });
    }

    var banner = await _dbContext.Banners
        .AsNoTracking()
        .SingleOrDefaultAsync(candidate => candidate.Id == bannerId, cancellationToken)
        ?? throw new KeyNotFoundException("No encontramos el banner.");

    var mediaUrls = await LoadMediaUrlsAsync([banner.ImageMediaAssetId], cancellationToken);
    return MapBanner(banner, mediaUrls);
  }

  public async Task<BannerResponse> CreateBannerAsync(CreateBannerRequest request, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(request);

    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
    ValidateBannerRequest(request.Title, request.Subtitle, request.LinkUrl, request.StartsAtUtc, request.EndsAtUtc, "imageMediaAssetId", errors);
    ThrowIfAnyValidationErrors(errors);

    // BUG-093: the file is checked under its row lock and the banner saved in the same transaction, so a
    // DELETE /media/{id} running at the same time either sees the banner as a reference or archives the file first
    // and this link is rejected.
    var banner = await MediaAssetReferences.InTransactionAsync(_dbContext, async () =>
    {
      await LockMediaAsync([request.ImageMediaAssetId], cancellationToken);
      await ValidateMediaAssetReferenceAsync(request.ImageMediaAssetId, MediaAssetPurpose.Banner, "imageMediaAssetId", errors, cancellationToken);
      ThrowIfAnyValidationErrors(errors);

      var created = NewBanner(request);
      _dbContext.Banners.Add(created);
      await _dbContext.SaveChangesAsync(cancellationToken);
      return created;
    }, cancellationToken);

    var mediaUrls = await LoadMediaUrlsAsync([banner.ImageMediaAssetId], cancellationToken);
    return MapBanner(banner, mediaUrls);
  }

  private Banner NewBanner(CreateBannerRequest request)
  {
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

    return banner;
  }

  public async Task<BannerResponse> UpdateBannerAsync(Guid bannerId, UpdateBannerRequest request, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(request);

    if (bannerId == Guid.Empty)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["bannerId"] = ["Falta el banner."]
      });
    }

    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
    ValidateBannerRequest(request.Title, request.Subtitle, request.LinkUrl, request.StartsAtUtc, request.EndsAtUtc, "imageMediaAssetId", errors);
    ThrowIfAnyValidationErrors(errors);

    var banner = await MediaAssetReferences.InTransactionAsync(_dbContext, async () =>
    {
      await LockMediaAsync([request.ImageMediaAssetId], cancellationToken);

      var existing = await _dbContext.Banners
          .SingleOrDefaultAsync(candidate => candidate.Id == bannerId, cancellationToken)
          ?? throw new KeyNotFoundException("No encontramos el banner.");

      await ValidateMediaAssetReferenceAsync(request.ImageMediaAssetId, MediaAssetPurpose.Banner, "imageMediaAssetId", errors, cancellationToken);
      ThrowIfAnyValidationErrors(errors);

      existing.Update(
          request.Title,
          request.Subtitle,
          request.ImageMediaAssetId,
          request.LinkUrl,
          request.SortOrder,
          request.IsActive,
          request.StartsAtUtc,
          request.EndsAtUtc,
          _timeProvider.GetUtcNow().UtcDateTime);

      await _dbContext.SaveChangesAsync(cancellationToken);
      return existing;
    }, cancellationToken);

    var mediaUrls = await LoadMediaUrlsAsync([banner.ImageMediaAssetId], cancellationToken);
    return MapBanner(banner, mediaUrls);
  }

  public async Task DeleteBannerAsync(Guid bannerId, CancellationToken cancellationToken = default)
  {
    if (bannerId == Guid.Empty)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["bannerId"] = ["Falta el banner."]
      });
    }

    var banner = await _dbContext.Banners
        .SingleOrDefaultAsync(candidate => candidate.Id == bannerId, cancellationToken)
        ?? throw new KeyNotFoundException("No encontramos el banner.");

    _dbContext.Banners.Remove(banner);
    await _dbContext.SaveChangesAsync(cancellationToken);
  }

  public async Task<IReadOnlyList<TickerItemResponse>> GetTickerItemsAsync(CancellationToken cancellationToken = default)
  {
    var items = await _dbContext.TickerItems
        .AsNoTracking()
        .OrderBy(item => item.SortOrder)
        .ThenBy(item => item.CreatedAt)
        .ToListAsync(cancellationToken);

    return items.Select(MapTickerItem).ToList();
  }

  public async Task<TickerItemResponse> GetTickerItemByIdAsync(Guid tickerItemId, CancellationToken cancellationToken = default)
  {
    if (tickerItemId == Guid.Empty)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["tickerItemId"] = ["Falta el aviso de la cinta."]
      });
    }

    var item = await _dbContext.TickerItems
        .AsNoTracking()
        .SingleOrDefaultAsync(candidate => candidate.Id == tickerItemId, cancellationToken)
        ?? throw new KeyNotFoundException("No encontramos el aviso de la cinta.");

    return MapTickerItem(item);
  }

  public async Task<TickerItemResponse> CreateTickerItemAsync(CreateTickerItemRequest request, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(request);

    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
    ValidateRequiredText(request.Text, "text", 1, 120, errors, "El texto es obligatorio y no puede superar 120 caracteres.");
    ThrowIfAnyValidationErrors(errors);

    var now = _timeProvider.GetUtcNow().UtcDateTime;

    var item = new TickerItem(request.Text, request.SortOrder, now);
    item.Update(request.Text, request.SortOrder, request.IsActive, now);

    _dbContext.TickerItems.Add(item);
    await _dbContext.SaveChangesAsync(cancellationToken);

    return MapTickerItem(item);
  }

  public async Task<TickerItemResponse> UpdateTickerItemAsync(Guid tickerItemId, UpdateTickerItemRequest request, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(request);

    if (tickerItemId == Guid.Empty)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["tickerItemId"] = ["Falta el aviso de la cinta."]
      });
    }

    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
    ValidateRequiredText(request.Text, "text", 1, 120, errors, "El texto es obligatorio y no puede superar 120 caracteres.");
    ThrowIfAnyValidationErrors(errors);

    var item = await _dbContext.TickerItems
        .SingleOrDefaultAsync(candidate => candidate.Id == tickerItemId, cancellationToken)
        ?? throw new KeyNotFoundException("No encontramos el aviso de la cinta.");

    var now = _timeProvider.GetUtcNow().UtcDateTime;
    item.Update(request.Text, request.SortOrder, request.IsActive, now);

    await _dbContext.SaveChangesAsync(cancellationToken);

    return MapTickerItem(item);
  }

  public async Task DeleteTickerItemAsync(Guid tickerItemId, CancellationToken cancellationToken = default)
  {
    if (tickerItemId == Guid.Empty)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["tickerItemId"] = ["Falta el aviso de la cinta."]
      });
    }

    var item = await _dbContext.TickerItems
        .SingleOrDefaultAsync(candidate => candidate.Id == tickerItemId, cancellationToken)
        ?? throw new KeyNotFoundException("No encontramos el aviso de la cinta.");

    _dbContext.TickerItems.Remove(item);
    await _dbContext.SaveChangesAsync(cancellationToken);
  }

  public async Task<IReadOnlyList<TickerItemResponse>> GetPublicTickerItemsAsync(CancellationToken cancellationToken = default)
  {
    var items = await _dbContext.TickerItems
        .AsNoTracking()
        .Where(item => item.IsActive)
        .OrderBy(item => item.SortOrder)
        .ThenBy(item => item.CreatedAt)
        .ToListAsync(cancellationToken);

    return items.Select(MapTickerItem).ToList();
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
    ValidateRequiredText(request.AppName, "appName", 2, 120, errors, "El nombre de la marca es obligatorio y debe tener entre 2 y 120 caracteres.");
    ValidateHexColor(request.PrimaryColor, "primaryColor", errors);
    ValidateHexColor(request.SecondaryColor, "secondaryColor", errors);
    ThrowIfAnyValidationErrors(errors);

    // BUG-093: same rule and same lock as banners (see CreateBannerAsync).
    var branding = await MediaAssetReferences.InTransactionAsync(_dbContext, async () =>
    {
      await LockMediaAsync([request.LogoMediaAssetId, request.AppIconMediaAssetId], cancellationToken);
      await ValidateMediaAssetReferenceAsync(request.LogoMediaAssetId, MediaAssetPurpose.Logo, "logoMediaAssetId", errors, cancellationToken);
      await ValidateMediaAssetReferenceAsync(request.AppIconMediaAssetId, MediaAssetPurpose.AppIcon, "appIconMediaAssetId", errors, cancellationToken);
      ThrowIfAnyValidationErrors(errors);

      var now = _timeProvider.GetUtcNow().UtcDateTime;
      var settings = await _dbContext.AppBrandingSettings
          .OrderByDescending(candidate => candidate.UpdatedAt ?? DateTime.MinValue)
          .FirstOrDefaultAsync(cancellationToken);

      if (settings is null)
      {
        settings = new AppBrandingSettings(request.AppName, request.PrimaryColor, request.SecondaryColor, now);
        _dbContext.AppBrandingSettings.Add(settings);
      }

      settings.Update(
          request.AppName,
          request.PrimaryColor,
          request.SecondaryColor,
          request.LogoMediaAssetId,
          request.AppIconMediaAssetId,
          now);

      await _dbContext.SaveChangesAsync(cancellationToken);
      return settings;
    }, cancellationToken);

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

  /// <summary>
  /// Public endpoints only show a file that is still a ready image of the slot's kind (BUG-093): links saved before
  /// the rule (a customer's attachment, a PDF, an archived file) stop being published without touching the data; the
  /// admin endpoints still show them so they can be replaced.
  /// </summary>
  private async Task<Dictionary<Guid, string?>> LoadPublishableMediaUrlsAsync(
      IEnumerable<(Guid? MediaAssetId, MediaAssetPurpose Purpose)> slots,
      CancellationToken cancellationToken)
  {
    var requested = slots
        .Where(slot => slot.MediaAssetId.HasValue && slot.MediaAssetId.Value != Guid.Empty)
        .Select(slot => (Id: slot.MediaAssetId!.Value, slot.Purpose))
        .ToHashSet();

    if (requested.Count == 0)
    {
      return [];
    }

    var ids = requested.Select(slot => slot.Id).Distinct().ToArray();
    var assets = await _dbContext.MediaAssets
        .AsNoTracking()
        .Where(media => ids.Contains(media.Id))
        .Select(media => new { media.Id, media.Purpose, media.Status, media.ContentType, media.PublicUrl })
        .ToListAsync(cancellationToken);

    return assets
        .Where(asset => requested.Contains((asset.Id, asset.Purpose)) && IsPublishable(asset.Status, asset.ContentType))
        .ToDictionary(asset => asset.Id, asset => asset.PublicUrl);
  }

  /// <summary>The raster formats the upload pipeline stores for images; never SVG (BUG-078) nor documents.</summary>
  private static readonly HashSet<string> PublishableImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
  {
    "image/jpeg",
    "image/png",
    "image/webp",
    "image/gif"
  };

  private static bool IsPublishable(MediaAssetStatus status, string contentType)
      => status == MediaAssetStatus.Ready && PublishableImageContentTypes.Contains(contentType);

  private Task LockMediaAsync(IEnumerable<Guid?> mediaAssetIds, CancellationToken cancellationToken)
      => MediaAssetReferences.LockAsync(
          _dbContext,
          mediaAssetIds.Where(id => id.HasValue && id.Value != Guid.Empty).Select(id => id!.Value).ToArray(),
          cancellationToken);

  /// <summary>
  /// A brand or banner slot may only point to a ready image uploaded for that slot (BUG-093): never a customer's
  /// private attachment, a document, a file of another kind or an archived one. Runs under the asset's row lock.
  /// Resending the file the slot already has follows the same rule, so a link saved before it must be replaced.
  /// </summary>
  private async Task ValidateMediaAssetReferenceAsync(
      Guid? mediaAssetId,
      MediaAssetPurpose slotPurpose,
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
      errors[fieldName] = ["El archivo elegido no es válido."];
      return;
    }

    var asset = await _dbContext.MediaAssets
        .AsNoTracking()
        .Where(media => media.Id == mediaAssetId.Value)
        .Select(media => new { media.Purpose, media.Status, media.ContentType })
        .SingleOrDefaultAsync(cancellationToken);

    if (asset is null)
    {
      errors[fieldName] = ["No encontramos el archivo elegido."];
      return;
    }

    if (asset.Purpose != slotPurpose || !IsPublishable(asset.Status, asset.ContentType))
    {
      errors[fieldName] = [$"Este archivo no se puede usar aquí. Elige una imagen disponible subida en Medios como «{SlotLabel(slotPurpose)}»."];
    }
  }

  private static string SlotLabel(MediaAssetPurpose purpose) => purpose switch
  {
    MediaAssetPurpose.Logo => "Logo",
    MediaAssetPurpose.AppIcon => "Ícono de la app",
    MediaAssetPurpose.Banner => "Banner",
    _ => purpose.ToString()
  };

  private const string LinkUrlMessage =
      "El enlace debe ser una dirección web completa que empiece por https:// o http:// (por ejemplo https://instagram.com/zona8).";

  /// <summary>BUG-047: the landing opens and embeds this link, so it must be a Google Maps address over https.</summary>
  private static void ValidateMapsUrl(string? mapsUrl, Dictionary<string, string[]> errors)
  {
    if (string.IsNullOrWhiteSpace(mapsUrl))
    {
      return;
    }

    if (mapsUrl.Trim().Length > WebUrls.MapsMaxLength)
    {
      errors["mapsUrl"] = [$"El enlace de Google Maps no puede superar {WebUrls.MapsMaxLength} caracteres."];
    }
    else if (!WebUrls.IsGoogleMapsUrl(mapsUrl))
    {
      errors["mapsUrl"] =
      [
        "La ubicación debe ser un enlace de Google Maps que empiece por https:// "
        + "(por ejemplo https://maps.app.goo.gl/… o https://www.google.com/maps/…)."
      ];
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
    ValidateRequiredText(title, "title", 2, 160, errors, "El título es obligatorio y debe tener entre 2 y 160 caracteres.");

    if (!string.IsNullOrWhiteSpace(subtitle) && subtitle.Trim().Length > 300)
    {
      errors["subtitle"] = ["El subtítulo no puede superar 300 caracteres."];
    }

    // BUG-096: the column holds 2048 characters; a longer link used to reach the database and answer 500.
    // BUG-047: only absolute http/https links; banners open them in a new tab as external pages.
    if (!string.IsNullOrWhiteSpace(linkUrl) && linkUrl.Trim().Length > WebUrls.MaxLength)
    {
      errors["linkUrl"] = [$"El enlace no puede superar {WebUrls.MaxLength} caracteres."];
    }
    else if (!string.IsNullOrWhiteSpace(linkUrl) && !WebUrls.IsHttpUrl(linkUrl))
    {
      errors["linkUrl"] = [LinkUrlMessage];
    }

    if (startsAtUtc.HasValue && endsAtUtc.HasValue && endsAtUtc.Value <= startsAtUtc.Value)
    {
      errors["endsAtUtc"] = ["La fecha de fin debe ser posterior a la de inicio."];
    }

    _ = imageFieldName;
  }

  private static void ValidateHexColor(string? value, string fieldName, Dictionary<string, string[]> errors)
  {
    if (string.IsNullOrWhiteSpace(value) || !HexColorRegex().IsMatch(value.Trim()))
    {
      errors[fieldName] = ["El color debe tener el formato #RRGGBB."];
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

  private static BannerResponse MapBanner(Banner banner, IReadOnlyDictionary<Guid, string?> mediaUrls, bool hideUnpublishable = false)
  {
    var imageUrl = ResolveMediaUrl(banner.ImageMediaAssetId, mediaUrls);

    return new BannerResponse(
        banner.Id,
        banner.Title,
        banner.Subtitle,
        PublishedId(banner.ImageMediaAssetId, mediaUrls, hideUnpublishable),
        imageUrl,
        banner.LinkUrl,
        banner.SortOrder,
        banner.IsActive,
        banner.StartsAt,
        banner.EndsAt,
        banner.CreatedAt,
        banner.UpdatedAt);
  }

  private static TickerItemResponse MapTickerItem(TickerItem item)
      => new(
          item.Id,
          item.Text,
          item.SortOrder,
          item.IsActive,
          item.CreatedAt,
          item.UpdatedAt);

  private static BrandingSettingsResponse MapBranding(AppBrandingSettings branding, IReadOnlyDictionary<Guid, string?> mediaUrls, bool hideUnpublishable = false)
      => new(
          branding.AppName,
          branding.PrimaryColor,
          branding.SecondaryColor,
          PublishedId(branding.LogoMediaAssetId, mediaUrls, hideUnpublishable),
          PublishedId(branding.AppIconMediaAssetId, mediaUrls, hideUnpublishable),
          ResolveMediaUrl(branding.LogoMediaAssetId, mediaUrls),
          ResolveMediaUrl(branding.AppIconMediaAssetId, mediaUrls),
          branding.UpdatedAt);

  /// <summary>On public endpoints a file that is not published is not named either.</summary>
  private static Guid? PublishedId(Guid? mediaAssetId, IReadOnlyDictionary<Guid, string?> mediaUrls, bool hideUnpublishable)
      => hideUnpublishable && mediaAssetId is { } id && !mediaUrls.ContainsKey(id) ? null : mediaAssetId;

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

    // BUG-097: a null element used to throw NullReferenceException (500).
    var missingDay = request.Days!.ToList().FindIndex(day => day is null);
    if (missingDay >= 0)
    {
      errors[$"days[{missingDay}]"] = ["Cada día debe tener datos: indica si abre y, si abre, su horario."];
      ThrowIfAnyValidationErrors(errors);
    }

    var orderedDayNumbers = request.Days!.Select(d => d.DayOfWeek).OrderBy(d => d).ToList();
    if (!orderedDayNumbers.SequenceEqual(Enumerable.Range(0, 7)))
    {
      errors["days"] = ["Los días deben ser exactamente 0-6 (lunes a domingo) sin duplicados."];
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
