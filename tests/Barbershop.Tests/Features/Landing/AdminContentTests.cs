using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Landing;
using Barbershop.Application.PublicContent;
using Barbershop.Domain.Media;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Landing;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Features.Landing;

public sealed class AdminContentTests : IDisposable
{
  private static readonly DateTimeOffset CurrentUtc = new(2026, 2, 10, 14, 0, 0, TimeSpan.Zero);

  private readonly AppDbContext _dbContext;
  private readonly IAdminContentService _adminContentService;

  public AdminContentTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    _adminContentService = new ContentManagementService(_dbContext, new FixedTimeProvider(CurrentUtc));
  }

  public void Dispose()
  {
    _dbContext.Dispose();
  }

  [Fact]
  public async Task UpsertLandingAsync_CreatesAndUpdatesSingleLandingRecord()
  {
    var first = await _adminContentService.UpsertLandingAsync(new UpsertLandingContentRequest(
        HeroTitle: "Reserva online",
        HeroSubtitle: "Turnos faciles",
        AboutTitle: "Sobre nosotros",
        AboutText: "Barberia de barrio",
        ContactPhone: "+541100000000",
        MapsUrl: "https://maps.google.com/?q=Calle+123",
        Address: "Calle 123"));

    var second = await _adminContentService.UpsertLandingAsync(new UpsertLandingContentRequest(
        HeroTitle: "Reserva ahora",
        HeroSubtitle: "Atencion profesional",
        AboutTitle: "Sobre nosotros",
        AboutText: "Nueva descripcion",
        ContactPhone: "+541111111111",
        MapsUrl: "https://maps.google.com/?q=Calle+999",
        Address: "Calle 999"));

    Assert.Equal("Reserva online", first.HeroTitle);
    Assert.Equal("Reserva ahora", second.HeroTitle);
    Assert.Equal("Atencion profesional", second.HeroSubtitle);
    Assert.Equal("Nueva descripcion", second.AboutText);
    Assert.Equal("+541111111111", second.ContactPhone);
    Assert.Equal("https://maps.google.com/?q=Calle+999", second.MapsUrl);

    Assert.Equal(1, await _dbContext.LandingContents.CountAsync());
  }

  [Fact]
  public async Task UpsertBrandingAsync_PersistsSettingsAndResolvesMediaUrls()
  {
    var logo = await CreateReadyMediaAssetAsync(MediaAssetPurpose.Logo);
    var appIcon = await CreateReadyMediaAssetAsync(MediaAssetPurpose.AppIcon);

    var response = await _adminContentService.UpsertBrandingAsync(new UpsertBrandingSettingsRequest(
        AppName: "Barbershop PWA",
        PrimaryColor: "#0A0A0A",
        SecondaryColor: "#F5B041",
        LogoMediaAssetId: logo.Id,
        AppIconMediaAssetId: appIcon.Id));

    Assert.Equal("Barbershop PWA", response.AppName);
    Assert.Equal("#0A0A0A", response.PrimaryColor);
    Assert.Equal("#F5B041", response.SecondaryColor);
    Assert.Equal(logo.Id, response.LogoMediaAssetId);
    Assert.Equal(appIcon.Id, response.AppIconMediaAssetId);
    Assert.Equal(logo.PublicUrl, response.LogoUrl);
    Assert.Equal(appIcon.PublicUrl, response.AppIconUrl);
    Assert.Equal(1, await _dbContext.AppBrandingSettings.CountAsync());
  }

  [Fact]
  public async Task UpsertBrandingAsync_RejectsUnknownMediaReference()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _adminContentService.UpsertBrandingAsync(new UpsertBrandingSettingsRequest(
            AppName: "Barbershop",
            PrimaryColor: "#010101",
            SecondaryColor: "#FFFFFF",
            LogoMediaAssetId: Guid.NewGuid(),
            AppIconMediaAssetId: null)));

    Assert.Contains("logoMediaAssetId", exception.Errors.Keys);
  }

  [Fact]
  public async Task UpsertBrandingAsync_RejectsInvalidHexColors()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _adminContentService.UpsertBrandingAsync(new UpsertBrandingSettingsRequest(
            AppName: "Barbershop",
            PrimaryColor: "blue",
            SecondaryColor: "#GGGGGG",
            LogoMediaAssetId: null,
            AppIconMediaAssetId: null)));

    Assert.Contains("primaryColor", exception.Errors.Keys);
    Assert.Contains("secondaryColor", exception.Errors.Keys);
  }

  [Fact]
  public async Task BannerCrudFlow_CreatesUpdatesReordersAndDeletes()
  {
    var media = await CreateReadyMediaAssetAsync(MediaAssetPurpose.Banner);

    var first = await _adminContentService.CreateBannerAsync(new CreateBannerRequest(
        Title: "Promo 1",
        Subtitle: "Primera promo",
        ImageMediaAssetId: media.Id,
        LinkUrl: "https://example.com/1",
        SortOrder: 5,
        IsActive: true,
        StartsAtUtc: CurrentUtc.UtcDateTime.AddHours(-1),
        EndsAtUtc: CurrentUtc.UtcDateTime.AddHours(1)));

    var second = await _adminContentService.CreateBannerAsync(new CreateBannerRequest(
        Title: "Promo 2",
        Subtitle: null,
        ImageMediaAssetId: null,
        LinkUrl: null,
        SortOrder: 1,
        IsActive: true,
        StartsAtUtc: null,
        EndsAtUtc: null));

    var ordered = await _adminContentService.GetBannersAsync();
    Assert.Collection(
        ordered,
        firstItem => Assert.Equal(second.Id, firstItem.Id),
        secondItem => Assert.Equal(first.Id, secondItem.Id));

    var updated = await _adminContentService.UpdateBannerAsync(first.Id, new UpdateBannerRequest(
        Title: "Promo 1 actualizada",
        Subtitle: "Promo destacada",
        ImageMediaAssetId: media.Id,
        LinkUrl: "https://example.com/updated",
        SortOrder: 0,
        IsActive: false,
        StartsAtUtc: null,
        EndsAtUtc: null));

    Assert.Equal("Promo 1 actualizada", updated.Title);
    Assert.Equal("Promo destacada", updated.Subtitle);
    Assert.Equal("https://example.com/updated", updated.LinkUrl);
    Assert.False(updated.IsActive);
    Assert.Equal(media.PublicUrl, updated.ImageUrl);

    await _adminContentService.DeleteBannerAsync(first.Id);

    await Assert.ThrowsAsync<KeyNotFoundException>(() => _adminContentService.GetBannerByIdAsync(first.Id));
    var remaining = await _adminContentService.GetBannersAsync();
    Assert.Single(remaining);
    Assert.Equal(second.Id, remaining[0].Id);
  }

  [Fact]
  public async Task TickerItemCrudFlow_CreatesUpdatesReordersAndDeletes()
  {
    var first = await _adminContentService.CreateTickerItemAsync(new CreateTickerItemRequest(
        Text: "El corte es oficio",
        SortOrder: 5,
        IsActive: true));

    var second = await _adminContentService.CreateTickerItemAsync(new CreateTickerItemRequest(
        Text: "Sin fila · Con cita",
        SortOrder: 1,
        IsActive: true));

    var ordered = await _adminContentService.GetTickerItemsAsync();
    Assert.Collection(
        ordered,
        firstItem => Assert.Equal(second.Id, firstItem.Id),
        secondItem => Assert.Equal(first.Id, secondItem.Id));

    var updated = await _adminContentService.UpdateTickerItemAsync(first.Id, new UpdateTickerItemRequest(
        Text: "Fades · Barba · Navaja · Diseño",
        SortOrder: 0,
        IsActive: false));

    Assert.Equal("Fades · Barba · Navaja · Diseño", updated.Text);
    Assert.False(updated.IsActive);

    await _adminContentService.DeleteTickerItemAsync(first.Id);

    await Assert.ThrowsAsync<KeyNotFoundException>(() => _adminContentService.GetTickerItemByIdAsync(first.Id));
    var remaining = await _adminContentService.GetTickerItemsAsync();
    Assert.Single(remaining);
    Assert.Equal(second.Id, remaining[0].Id);
  }

  [Fact]
  public async Task CreateTickerItemAsync_RejectsEmptyText()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _adminContentService.CreateTickerItemAsync(new CreateTickerItemRequest(
            Text: "   ",
            SortOrder: 0,
            IsActive: true)));

    Assert.Contains("text", exception.Errors.Keys);
  }

  [Fact]
  public async Task CreateBannerAsync_RejectsInvalidDateRange()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _adminContentService.CreateBannerAsync(new CreateBannerRequest(
            Title: "Invalid",
            Subtitle: null,
            ImageMediaAssetId: null,
            LinkUrl: null,
            SortOrder: 0,
            IsActive: true,
            StartsAtUtc: CurrentUtc.UtcDateTime,
            EndsAtUtc: CurrentUtc.UtcDateTime.AddMinutes(-1))));

    Assert.Contains("endsAtUtc", exception.Errors.Keys);
  }

  [Fact]
  public async Task CreateBannerAsync_RejectsUnknownImageMediaAsset()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _adminContentService.CreateBannerAsync(new CreateBannerRequest(
            Title: "Invalid Media",
            Subtitle: null,
            ImageMediaAssetId: Guid.NewGuid(),
            LinkUrl: null,
            SortOrder: 1,
            IsActive: true,
            StartsAtUtc: null,
            EndsAtUtc: null)));

    Assert.Contains("imageMediaAssetId", exception.Errors.Keys);
  }

  // ── BUG-093: brand and banner slots only take a ready image uploaded for that slot ──

  public static TheoryData<MediaAssetPurpose> PurposesThatAreNotALogo => new()
  {
    MediaAssetPurpose.CustomerReference,
    MediaAssetPurpose.Other,
    MediaAssetPurpose.AppIcon,
    MediaAssetPurpose.Banner,
    MediaAssetPurpose.StaffPhoto,
    MediaAssetPurpose.TipsQr,
    MediaAssetPurpose.ProductImage
  };

  [Theory]
  [MemberData(nameof(PurposesThatAreNotALogo))]
  public async Task UpsertBrandingAsync_RejectsALogoOfAnotherKind_IncludingACustomersPrivateAttachment(MediaAssetPurpose purpose)
  {
    var file = await CreateReadyMediaAssetAsync(purpose);

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _adminContentService.UpsertBrandingAsync(Branding(logoId: file.Id)));

    Assert.Contains("logoMediaAssetId", exception.Errors.Keys);
    Assert.Empty(await _dbContext.AppBrandingSettings.ToListAsync());
  }

  [Fact]
  public async Task UpsertBrandingAsync_RejectsACustomersAttachmentAsAppIcon()
  {
    var attachment = await CreateReadyMediaAssetAsync(MediaAssetPurpose.CustomerReference);

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _adminContentService.UpsertBrandingAsync(Branding(appIconId: attachment.Id)));

    Assert.Contains("appIconMediaAssetId", exception.Errors.Keys);
  }

  [Fact]
  public async Task UpsertBrandingAsync_RejectsAPdfEvenWithTheLogoPurpose()
  {
    var pdf = await CreateReadyMediaAssetAsync(MediaAssetPurpose.Logo, contentType: "application/pdf");

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _adminContentService.UpsertBrandingAsync(Branding(logoId: pdf.Id)));

    Assert.Contains("logoMediaAssetId", exception.Errors.Keys);
  }

  [Theory]
  [InlineData(MediaAssetStatus.Archived)]
  [InlineData(MediaAssetStatus.Pending)]
  [InlineData(MediaAssetStatus.Failed)]
  public async Task CreateBannerAsync_RejectsAFileThatIsNotReady(MediaAssetStatus status)
  {
    var file = await CreateReadyMediaAssetAsync(MediaAssetPurpose.Banner, status: status);

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _adminContentService.CreateBannerAsync(BannerRequest(file.Id)));

    Assert.Contains("imageMediaAssetId", exception.Errors.Keys);
    Assert.Empty(await _dbContext.Banners.ToListAsync());
  }

  [Theory]
  [InlineData(MediaAssetPurpose.CustomerReference)]
  [InlineData(MediaAssetPurpose.Other)]
  [InlineData(MediaAssetPurpose.Logo)]
  public async Task CreateAndUpdateBannerAsync_RejectAnImageOfAnotherKind(MediaAssetPurpose purpose)
  {
    var file = await CreateReadyMediaAssetAsync(purpose);
    var banner = await _adminContentService.CreateBannerAsync(BannerRequest(null));

    var onCreate = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _adminContentService.CreateBannerAsync(BannerRequest(file.Id)));
    var onUpdate = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _adminContentService.UpdateBannerAsync(banner.Id, new UpdateBannerRequest(
            "Promo", null, file.Id, null, 1, true, null, null)));

    Assert.Contains("imageMediaAssetId", onCreate.Errors.Keys);
    Assert.Contains("imageMediaAssetId", onUpdate.Errors.Keys);
    Assert.Null((await _dbContext.Banners.AsNoTracking().SingleAsync()).ImageMediaAssetId);
  }

  [Fact]
  public async Task UpdateBannerAsync_KeepingAnImageLinkedBeforeTheRule_MustReplaceIt()
  {
    var attachment = await CreateReadyMediaAssetAsync(MediaAssetPurpose.CustomerReference);
    var banner = new Barbershop.Domain.Landing.Banner("Promo", 1, CurrentUtc.UtcDateTime, null, attachment.Id, null);
    _dbContext.Banners.Add(banner);
    await _dbContext.SaveChangesAsync();

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _adminContentService.UpdateBannerAsync(banner.Id, new UpdateBannerRequest(
            "Promo editada", null, attachment.Id, null, 1, true, null, null)));

    Assert.Contains("imageMediaAssetId", exception.Errors.Keys);
  }

  [Fact]
  public async Task BrandAndBannerImages_OfTheirOwnKind_AreAccepted_AndTheFileCannotBeDeletedWhileLinked()
  {
    var logo = await CreateReadyMediaAssetAsync(MediaAssetPurpose.Logo);
    var icon = await CreateReadyMediaAssetAsync(MediaAssetPurpose.AppIcon);
    var image = await CreateReadyMediaAssetAsync(MediaAssetPurpose.Banner);

    var branding = await _adminContentService.UpsertBrandingAsync(Branding(logoId: logo.Id, appIconId: icon.Id));
    var banner = await _adminContentService.CreateBannerAsync(BannerRequest(image.Id));

    Assert.Equal(logo.PublicUrl, branding.LogoUrl);
    Assert.Equal(icon.PublicUrl, branding.AppIconUrl);
    Assert.Equal(image.PublicUrl, banner.ImageUrl);
    foreach (var id in new[] { logo.Id, icon.Id, image.Id })
    {
      Assert.True(await Barbershop.Infrastructure.Media.MediaAssetReferences.IsReferencedAsync(_dbContext, id, CancellationToken.None));
    }
  }

  private static UpsertBrandingSettingsRequest Branding(Guid? logoId = null, Guid? appIconId = null)
      => new("Barbershop", "#010101", "#FFFFFF", logoId, appIconId);

  private static CreateBannerRequest BannerRequest(Guid? imageId)
      => new("Promo", null, imageId, null, 1, true, null, null);

  private async Task<MediaAsset> CreateReadyMediaAssetAsync(
      MediaAssetPurpose purpose,
      string contentType = "image/png",
      MediaAssetStatus status = MediaAssetStatus.Ready)
  {
    var createdAt = CurrentUtc.UtcDateTime;
    var user = new User(
        "Admin Content",
        $"admin-content-{Guid.NewGuid():N}@example.com",
        "hashed-password",
        createdAt);

    var media = new MediaAsset(
        fileName: $"asset-{Guid.NewGuid():N}.png",
        contentType: contentType,
        sizeBytes: 2_048,
        storageKey: $"branding/{Guid.NewGuid():N}.png",
        purpose: purpose,
        uploadedByUserId: user.Id,
        createdAt: createdAt);

    switch (status)
    {
      case MediaAssetStatus.Ready:
        media.MarkReady($"https://assets.example.com/{media.StorageKey}", createdAt.AddMinutes(1));
        break;
      case MediaAssetStatus.Archived:
        media.MarkReady($"https://assets.example.com/{media.StorageKey}", createdAt.AddMinutes(1));
        media.Archive(createdAt.AddMinutes(2));
        break;
      case MediaAssetStatus.Failed:
        media.MarkFailed("storage down", createdAt.AddMinutes(1));
        break;
    }

    _dbContext.Users.Add(user);
    _dbContext.MediaAssets.Add(media);
    await _dbContext.SaveChangesAsync();

    return media;
  }

  // ── BUG-047 / BUG-096: links the public landing publishes ──

  /// <summary>BUG-047: unsafe schemes and relative links ("/staff" was stored as file:///staff) are rejected.</summary>
  [Theory]
  [InlineData("javascript:alert(document.domain)")]
  [InlineData("data:text/html,<script>alert(1)</script>")]
  [InlineData("vbscript:msgbox(1)")]
  [InlineData("file:///etc/passwd")]
  [InlineData("/staff")]
  [InlineData("example.com")]
  [InlineData("ftp://example.com/file")]
  [InlineData("https://user:secret@example.com/")]
  public async Task CreateAndUpdateBannerAsync_RejectALinkThatIsNotHttp(string linkUrl)
  {
    var create = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _adminContentService.CreateBannerAsync(NewBanner("Enlace inseguro", linkUrl)));
    Assert.Contains("linkUrl", create.Errors.Keys);

    var banner = await _adminContentService.CreateBannerAsync(NewBanner("Enlace seguro", "https://example.com/promo"));
    var update = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _adminContentService.UpdateBannerAsync(banner.Id, new UpdateBannerRequest(
            "Enlace seguro", null, null, linkUrl, 1, false, null, null)));
    Assert.Contains("linkUrl", update.Errors.Keys);
    Assert.Equal("https://example.com/promo", (await _dbContext.Banners.AsNoTracking().SingleAsync()).LinkUrl);
  }

  /// <summary>BUG-096: a 2100-character link used to overflow the 2048 column and answer 500.</summary>
  [Fact]
  public async Task CreateBannerAsync_RejectsALinkLongerThanTheColumn_WithASpanishMessage()
  {
    var tooLong = "https://example.com/" + new string('a', 2100 - "https://example.com/".Length);

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _adminContentService.CreateBannerAsync(NewBanner("Enlace largo", tooLong)));

    Assert.Contains("2048 caracteres", Assert.Single(exception.Errors["linkUrl"]));
    Assert.Empty(await _dbContext.Banners.ToListAsync());
  }

  [Fact]
  public async Task CreateBannerAsync_AcceptsAnHttpLinkOfExactlyTheColumnLength()
  {
    var atLimit = "https://example.com/" + new string('a', 2048 - "https://example.com/".Length);

    var banner = await _adminContentService.CreateBannerAsync(NewBanner("Enlace al límite", atLimit));

    Assert.Equal(atLimit, banner.LinkUrl);
  }

  /// <summary>BUG-047: the map link is opened and embedded by the landing; only Google Maps over https.</summary>
  [Theory]
  [InlineData("javascript:alert(1)")]
  [InlineData("data:text/html,<script>alert(1)</script>")]
  [InlineData("maps.google.com/?q=Calle+1")]
  [InlineData("http://maps.google.com/?q=Calle+1")]
  [InlineData("https://evil.example.com/maps?q=1")]
  [InlineData("https://www.google.com/search?q=barberia")]
  [InlineData("https://google.com.evil.example/maps")]
  public async Task UpsertLandingAsync_RejectsAMapLinkThatIsNotGoogleMapsOverHttps(string mapsUrl)
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _adminContentService.UpsertLandingAsync(NewLanding(mapsUrl)));

    Assert.Contains("mapsUrl", exception.Errors.Keys);
  }

  [Theory]
  [InlineData("https://maps.app.goo.gl/AbCdEf123")]
  [InlineData("https://goo.gl/maps/AbCdEf123")]
  [InlineData("https://www.google.com/maps/place/Zona+8/@4.6,-74.1,17z")]
  [InlineData("https://www.google.com/maps/embed?pb=!1m18!1m12")]
  [InlineData("https://maps.google.com/?q=Calle+123")]
  [InlineData("https://www.google.com.co/maps/place/Zona+8")]
  public async Task UpsertLandingAsync_AcceptsGoogleMapsLinks(string mapsUrl)
  {
    var landing = await _adminContentService.UpsertLandingAsync(NewLanding(mapsUrl));

    Assert.Equal(mapsUrl, landing.MapsUrl);
  }

  private static CreateBannerRequest NewBanner(string title, string? linkUrl)
      => new(title, null, null, linkUrl, 1, false, null, null);

  private static UpsertLandingContentRequest NewLanding(string? mapsUrl)
      => new("Reserva online", null, null, null, null, mapsUrl, null);

  private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
  {
    public override DateTimeOffset GetUtcNow() => utcNow;
  }
}
