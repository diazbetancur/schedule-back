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

  private async Task<MediaAsset> CreateReadyMediaAssetAsync(MediaAssetPurpose purpose)
  {
    var createdAt = CurrentUtc.UtcDateTime;
    var user = new User(
        "Admin Content",
        $"admin-content-{Guid.NewGuid():N}@example.com",
        "hashed-password",
        createdAt);

    var media = new MediaAsset(
        fileName: $"asset-{Guid.NewGuid():N}.png",
        contentType: "image/png",
        sizeBytes: 2_048,
        storageKey: $"branding/{Guid.NewGuid():N}.png",
        purpose: purpose,
        uploadedByUserId: user.Id,
        createdAt: createdAt);

    media.MarkReady($"https://assets.example.com/{media.StorageKey}", createdAt.AddMinutes(1));

    _dbContext.Users.Add(user);
    _dbContext.MediaAssets.Add(media);
    await _dbContext.SaveChangesAsync();

    return media;
  }

  private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
  {
    public override DateTimeOffset GetUtcNow() => utcNow;
  }
}
