using Barbershop.Application.PublicContent;
using Barbershop.Application.Storage;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Landing;
using Barbershop.Domain.Media;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Landing;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Staff;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Features.Public;

public sealed class PublicContentTests : IDisposable
{
  private static readonly DateTimeOffset CurrentUtc = new(2026, 2, 10, 12, 0, 0, TimeSpan.Zero);

  private readonly AppDbContext _dbContext;
  private readonly IPublicContentService _publicContentService;
  private readonly IPublicStaffService _publicStaffService;

  public PublicContentTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    _publicContentService = new ContentManagementService(_dbContext, new FixedTimeProvider(CurrentUtc));
    _publicStaffService = new PublicStaffService(_dbContext, new NullFileStorageService());
  }

  public void Dispose()
  {
    _dbContext.Dispose();
  }

  /// <summary>Stub de IFileStorageService para tests: siempre retorna null como URL pública.</summary>
  private sealed class NullFileStorageService : IFileStorageService
  {
    public Task<StoredFileResult> UploadAsync(FileStorageObject file, CancellationToken cancellationToken = default)
        => Task.FromResult(new StoredFileResult(file.ObjectKey, null));

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public string? GetPublicUrl(string storageKey) => null;
  }

  [Fact]
  public async Task GetPublicLandingAsync_ReturnsDefaultPayload_WhenNoContentExists()
  {
    var landing = await _publicContentService.GetPublicLandingAsync();

    Assert.Equal("Reserva tu proximo turno", landing.HeroTitle);
    Assert.Null(landing.HeroSubtitle);
    Assert.Null(landing.AboutTitle);
    Assert.Null(landing.AboutText);
    Assert.Null(landing.ContactPhone);
    Assert.Null(landing.MapsUrl);
    Assert.Null(landing.Address);
  }

  [Fact]
  public async Task GetPublicBrandingAsync_ReturnsDefaultPayload_WhenNoSettingsExist()
  {
    var branding = await _publicContentService.GetPublicBrandingAsync();

    Assert.Equal("Barbershop", branding.AppName);
    Assert.Equal("#111111", branding.PrimaryColor);
    Assert.Equal("#C59D5F", branding.SecondaryColor);
    Assert.Null(branding.LogoMediaAssetId);
    Assert.Null(branding.AppIconMediaAssetId);
    Assert.Null(branding.LogoUrl);
    Assert.Null(branding.AppIconUrl);
  }

  [Fact]
  public async Task GetPublicBannersAsync_ReturnsOnlyActiveCurrentBanners_OrderedBySortOrder()
  {
    var now = CurrentUtc.UtcDateTime;
    var imageAsset = await CreateReadyMediaAssetAsync(MediaAssetPurpose.Banner);

    var firstVisible = CreateBanner(
        title: "Visible First",
        subtitle: "Shown first",
        sortOrder: 1,
        isActive: true,
        createdAt: now.AddMinutes(-15),
        startsAtUtc: now.AddHours(-2),
        endsAtUtc: now.AddHours(2),
        imageMediaAssetId: imageAsset.Id,
        linkUrl: "https://example.com/first");

    var secondVisible = CreateBanner(
        title: "Visible Second",
        subtitle: null,
        sortOrder: 2,
        isActive: true,
        createdAt: now.AddMinutes(-10),
        startsAtUtc: null,
        endsAtUtc: null,
        imageMediaAssetId: null,
        linkUrl: null);

    var inactive = CreateBanner(
        title: "Inactive",
        subtitle: null,
        sortOrder: 0,
        isActive: false,
        createdAt: now.AddMinutes(-20),
        startsAtUtc: null,
        endsAtUtc: null,
        imageMediaAssetId: null,
        linkUrl: null);

    var startsInFuture = CreateBanner(
        title: "Future",
        subtitle: null,
        sortOrder: 3,
        isActive: true,
        createdAt: now.AddMinutes(-5),
        startsAtUtc: now.AddMinutes(5),
        endsAtUtc: now.AddHours(1),
        imageMediaAssetId: null,
        linkUrl: null);

    var expired = CreateBanner(
        title: "Expired",
        subtitle: null,
        sortOrder: 4,
        isActive: true,
        createdAt: now.AddMinutes(-30),
        startsAtUtc: now.AddHours(-3),
        endsAtUtc: now.AddMinutes(-1),
        imageMediaAssetId: null,
        linkUrl: null);

    _dbContext.Banners.AddRange(firstVisible, secondVisible, inactive, startsInFuture, expired);
    await _dbContext.SaveChangesAsync();

    var banners = await _publicContentService.GetPublicBannersAsync();

    Assert.Equal(2, banners.Count);
    Assert.Collection(
        banners,
        first =>
        {
          Assert.Equal(firstVisible.Id, first.Id);
          Assert.Equal(imageAsset.PublicUrl, first.ImageUrl);
        },
        second => Assert.Equal(secondVisible.Id, second.Id));
  }

  [Fact]
  public async Task GetPublicStaffAsync_ReturnsOnlyActiveStaffProfiles()
  {
    var visibleStaff = await CreateStaffProfileAsync("Visible Barber", isStaffActive: true, isUserActive: true);
    _ = await CreateStaffProfileAsync("Inactive Profile", isStaffActive: false, isUserActive: true);
    _ = await CreateStaffProfileAsync("Inactive User", isStaffActive: true, isUserActive: false);

    var staff = await _publicStaffService.GetPublicStaffAsync();

    var item = Assert.Single(staff);
    Assert.Equal(visibleStaff.Id, item.StaffProfileId);
    Assert.Equal("Visible Barber", item.DisplayName);
  }

  [Fact]
  public async Task GetPublicStaffAsync_IncludesPhotoUrl_WhenMediaExists()
  {
    var photoAsset = await CreateReadyMediaAssetAsync(MediaAssetPurpose.StaffPhoto);
    var visibleStaff = await CreateStaffProfileAsync(
        "Photo Barber",
        isStaffActive: true,
        isUserActive: true,
        photoMediaAssetId: photoAsset.Id);

    var staff = await _publicStaffService.GetPublicStaffAsync();

    var item = Assert.Single(staff);
    Assert.Equal(visibleStaff.Id, item.StaffProfileId);
    Assert.Equal(photoAsset.Id, item.PhotoMediaAssetId);
    Assert.Equal(photoAsset.PublicUrl, item.PhotoUrl);
  }

  [Fact]
  public async Task GetPublicStaffByIdAsync_ReturnsActiveStaffDetails()
  {
    var photoAsset = await CreateReadyMediaAssetAsync(MediaAssetPurpose.StaffPhoto);
    var tipsQrAsset = await CreateReadyMediaAssetAsync(MediaAssetPurpose.TipsQr);

    var staffProfile = await CreateStaffProfileAsync(
        "Profile Barber",
        isStaffActive: true,
        isUserActive: true,
        bio: "Profile bio",
        phoneNumber: "+5491100001111",
        photoMediaAssetId: photoAsset.Id,
        tipsQrMediaAssetId: tipsQrAsset.Id,
        durationMinutes: 45);

    var profile = await _publicStaffService.GetPublicStaffByIdAsync(staffProfile.Id);

    Assert.Equal(staffProfile.Id, profile.StaffProfileId);
    Assert.Equal("Profile Barber", profile.DisplayName);
    Assert.Equal("Profile bio", profile.Bio);
    Assert.Equal("+5491100001111", profile.PhoneNumber);
    Assert.Equal(photoAsset.Id, profile.PhotoMediaAssetId);
    Assert.Equal(photoAsset.PublicUrl, profile.PhotoUrl);
    Assert.Equal(tipsQrAsset.Id, profile.TipsQrMediaAssetId);
    Assert.Equal(tipsQrAsset.PublicUrl, profile.TipsQrUrl);
    Assert.Equal(45, profile.DefaultAppointmentDurationMinutes);
  }

  [Fact]
  public async Task GetPublicStaffByIdAsync_ThrowsNotFound_WhenStaffIsInactive()
  {
    var inactiveStaff = await CreateStaffProfileAsync("Inactive Barber", isStaffActive: false, isUserActive: true);

    await Assert.ThrowsAsync<KeyNotFoundException>(() =>
        _publicStaffService.GetPublicStaffByIdAsync(inactiveStaff.Id));
  }

  [Fact]
  public async Task GetPublicStaffByIdAsync_IncludesOnlyActiveServices()
  {
    var staffProfile = await CreateStaffProfileAsync("Service Barber", isStaffActive: true, isUserActive: true);
    var activeService = await CreateStaffServiceAsync(staffProfile.Id, "Classic Cut", true, "Classic fade", 30, 12m);
    _ = await CreateStaffServiceAsync(staffProfile.Id, "Archived Service", false, "Not visible", 45, 18m);

    var profile = await _publicStaffService.GetPublicStaffByIdAsync(staffProfile.Id);

    var service = Assert.Single(profile.Services);
    Assert.Equal(activeService.Id, service.Id);
    Assert.Equal("Classic Cut", service.Name);
    Assert.Equal("Classic fade", service.Description);
    Assert.Equal(30, service.DurationMinutes);
    Assert.Equal(12m, service.Price);
    Assert.True(service.IsActive);
  }

  [Fact]
  public async Task GetPublicStaffByIdAsync_IncludesReviewSummary_WhenReviewsExist()
  {
    var staffProfile = await CreateStaffProfileAsync("Rated Barber", isStaffActive: true, isUserActive: true);
    await AddCompletedReviewAsync(staffProfile, 4, "Great");
    await AddCompletedReviewAsync(staffProfile, 5, "Excellent");

    var profile = await _publicStaffService.GetPublicStaffByIdAsync(staffProfile.Id);

    Assert.Equal(2, profile.ReviewCount);
    Assert.Equal(4.5m, profile.AverageRating);
  }

  private async Task<MediaAsset> CreateReadyMediaAssetAsync(MediaAssetPurpose purpose)
  {
    var createdAt = CurrentUtc.UtcDateTime;
    var user = new User(
        "Content Admin",
        $"content-admin-{Guid.NewGuid():N}@example.com",
        "hashed-password",
        createdAt);

    var media = new MediaAsset(
        fileName: $"asset-{Guid.NewGuid():N}.png",
        contentType: "image/png",
        sizeBytes: 1_024,
        storageKey: $"content/{Guid.NewGuid():N}.png",
        purpose: purpose,
        uploadedByUserId: user.Id,
        createdAt: createdAt);

    media.MarkReady($"https://assets.example.com/{media.StorageKey}", createdAt.AddMinutes(1));

    _dbContext.Users.Add(user);
    _dbContext.MediaAssets.Add(media);
    await _dbContext.SaveChangesAsync();

    return media;
  }

  private async Task<StaffProfile> CreateStaffProfileAsync(
      string displayName,
      bool isStaffActive,
      bool isUserActive,
      string? bio = null,
      string? phoneNumber = null,
      Guid? photoMediaAssetId = null,
      Guid? tipsQrMediaAssetId = null,
      int durationMinutes = 30)
  {
    var createdAt = CurrentUtc.UtcDateTime.AddMinutes(-5);
    var user = new User(
        fullName: $"{displayName} User",
        email: $"{Guid.NewGuid():N}@example.com",
        passwordHash: "hashed-password",
        createdAt: createdAt,
        phoneNumber: phoneNumber);

    if (!isUserActive)
    {
      user.Deactivate(createdAt.AddMinutes(1));
    }

    var staffProfile = new StaffProfile(user.Id, displayName, durationMinutes, createdAt);
    staffProfile.UpdateDetails(
        displayName,
        bio,
        phoneNumber,
        photoMediaAssetId,
        tipsQrMediaAssetId,
        durationMinutes,
        isStaffActive,
        createdAt.AddMinutes(2));

    _dbContext.Users.Add(user);
    _dbContext.StaffProfiles.Add(staffProfile);
    await _dbContext.SaveChangesAsync();

    return staffProfile;
  }

  private async Task<StaffService> CreateStaffServiceAsync(
      Guid staffProfileId,
      string name,
      bool isActive,
      string? description,
      int durationMinutes,
      decimal? price)
  {
    var createdAt = CurrentUtc.UtcDateTime.AddMinutes(-3);
    var service = new StaffService(staffProfileId, name, durationMinutes, price, createdAt, description);

    if (!isActive)
    {
      service.Update(name, description, durationMinutes, price, false, createdAt.AddMinutes(1));
    }

    _dbContext.StaffServices.Add(service);
    await _dbContext.SaveChangesAsync();

    return service;
  }

  private async Task AddCompletedReviewAsync(StaffProfile staffProfile, int stars, string? comment)
  {
    var createdAt = CurrentUtc.UtcDateTime.AddHours(-1);
    var customer = new User(
        fullName: $"Customer {Guid.NewGuid():N}",
        email: $"customer-{Guid.NewGuid():N}@example.com",
        passwordHash: "hashed-password",
        createdAt: createdAt,
        phoneNumber: "+5491100000000");

    var appointment = new Appointment(
        staffProfileId: staffProfile.Id,
        customerName: customer.FullName,
        startsAt: createdAt,
        endsAt: createdAt.AddMinutes(staffProfile.DefaultAppointmentDurationMinutes),
        status: AppointmentStatus.Completed,
        source: AppointmentSource.CustomerBooking,
        createdAt: createdAt,
        customerUserId: customer.Id,
        customerEmail: customer.Email,
        customerPhone: customer.PhoneNumber,
        notes: null);

    var review = new Review(
        appointmentId: appointment.Id,
        customerUserId: customer.Id,
        stars: stars,
        createdAt: createdAt.AddMinutes(5),
        comment: comment);

    _dbContext.Users.Add(customer);
    _dbContext.Appointments.Add(appointment);
    _dbContext.Reviews.Add(review);
    await _dbContext.SaveChangesAsync();
  }

  private static Banner CreateBanner(
      string title,
      string? subtitle,
      int sortOrder,
      bool isActive,
      DateTime createdAt,
      DateTime? startsAtUtc,
      DateTime? endsAtUtc,
      Guid? imageMediaAssetId,
      string? linkUrl)
  {
    var banner = new Banner(title, sortOrder, createdAt, subtitle, imageMediaAssetId, linkUrl);
    banner.Update(title, subtitle, imageMediaAssetId, linkUrl, sortOrder, isActive, startsAtUtc, endsAtUtc, createdAt.AddMinutes(1));
    return banner;
  }

  private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
  {
    public override DateTimeOffset GetUtcNow() => utcNow;
  }
}
