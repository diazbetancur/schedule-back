using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Media;
using Barbershop.Application.Staff;
using Barbershop.Application.Staff.Admin;
using Barbershop.Application.Staff.SelfService;
using Barbershop.Application.Storage;
using Barbershop.Domain.Landing;
using Barbershop.Domain.Media;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Configuration;
using Barbershop.Infrastructure.Customer;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Staff;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Barbershop.Tests.Features.Staff;

/// <summary>
/// BUG-019 (SONDA): a professional could point their photo/QR to any file of the platform by id and then delete it
/// from storage. Linking now requires a ready file of the slot's kind, not used elsewhere and, in self-service,
/// uploaded by the professional; replacing or removing only deletes files the slot legitimately owns. Storage is a
/// recording fake: no real file is ever touched.
/// </summary>
public sealed class StaffMediaOwnershipTests : IDisposable
{
  private readonly AppDbContext _dbContext;
  private readonly IAdminStaffService _adminStaffService;
  private readonly IStaffProfileService _staffProfileService;
  private readonly CustomerProfileService _customerProfileService;
  private readonly IdentitySeedService _seedService;
  private readonly RecordingFileStorageService _storage = new();

  public StaffMediaOwnershipTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;
    _dbContext = new AppDbContext(options);

    var passwordHasher = new PasswordHasher<object>();
    _seedService = new IdentitySeedService(
        _dbContext, passwordHasher, Options.Create(new SeedAdminOptions()), new TestHostEnvironment(), TimeProvider.System);

    var mediaAssets = new ReadyMediaAssetsService(_dbContext);
    var staffService = new StaffManagementService(
        _dbContext, _seedService, passwordHasher, TimeProvider.System, mediaAssets, _storage);
    _adminStaffService = staffService;
    _staffProfileService = staffService;
    _customerProfileService = new CustomerProfileService(_dbContext, passwordHasher, mediaAssets, _storage, TimeProvider.System);
  }

  public void Dispose() => _dbContext.Dispose();

  // ── Linking through PUT /staff/profile (self-service) ──────────────

  [Fact]
  public async Task SelfUpdate_LinkingAnotherProfessionalsPhoto_IsRejectedWithoutTouchingStorage()
  {
    var attacker = await CreateStaffAsync("attacker@example.com");
    var victim = await CreateStaffAsync("victim@example.com");
    var victimPhoto = await AddAssetAsync(MediaAssetPurpose.StaffPhoto, victim.UserId);

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _staffProfileService.UpdateCurrentAsync(attacker.UserId, SelfUpdate(photoId: victimPhoto)));

    Assert.True(exception.Errors.ContainsKey("photoMediaAssetId"));
    await AssertPhotoAsync(attacker.StaffProfileId, null);
    Assert.Empty(_storage.DeletedKeys);
  }

  [Fact]
  public async Task SelfUpdate_LinkingACustomersPrivateAttachment_IsRejected()
  {
    var attacker = await CreateStaffAsync("attacker@example.com");
    var customerId = await CreateUserAsync("customer@example.com", RoleNames.Customer);
    var attachment = await AddAssetAsync(MediaAssetPurpose.CustomerReference, customerId);

    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _staffProfileService.UpdateCurrentAsync(attacker.UserId, SelfUpdate(photoId: attachment)));
    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _staffProfileService.UpdateCurrentAsync(attacker.UserId, SelfUpdate(tipsQrId: attachment)));

    await AssertPhotoAsync(attacker.StaffProfileId, null);
    var user = await _dbContext.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == attacker.UserId);
    Assert.Null(user.ProfilePhotoMediaAssetId);
  }

  [Theory]
  [InlineData(MediaAssetPurpose.Logo)]
  [InlineData(MediaAssetPurpose.Banner)]
  [InlineData(MediaAssetPurpose.ProductImage)]
  [InlineData(MediaAssetPurpose.AppIcon)]
  public async Task SelfUpdate_LinkingBrandOrCatalogFiles_IsRejected(MediaAssetPurpose purpose)
  {
    var attacker = await CreateStaffAsync("attacker@example.com");
    var adminId = await CreateUserAsync("admin@example.com", RoleNames.Admin);
    var brandFile = await AddAssetAsync(purpose, adminId);

    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _staffProfileService.UpdateCurrentAsync(attacker.UserId, SelfUpdate(photoId: brandFile)));
  }

  [Fact]
  public async Task SelfUpdate_OwnFileOfTheOtherSlotsKind_IsRejected()
  {
    var staff = await CreateStaffAsync("staff@example.com");
    var ownQr = await AddAssetAsync(MediaAssetPurpose.TipsQr, staff.UserId);
    var ownPhoto = await AddAssetAsync(MediaAssetPurpose.StaffPhoto, staff.UserId);

    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _staffProfileService.UpdateCurrentAsync(staff.UserId, SelfUpdate(photoId: ownQr)));
    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _staffProfileService.UpdateCurrentAsync(staff.UserId, SelfUpdate(tipsQrId: ownPhoto)));
  }

  [Theory]
  [InlineData(MediaAssetStatus.Pending)]
  [InlineData(MediaAssetStatus.Failed)]
  [InlineData(MediaAssetStatus.Archived)]
  public async Task SelfUpdate_OwnFileThatIsNotReady_IsRejected(MediaAssetStatus status)
  {
    var staff = await CreateStaffAsync("staff@example.com");
    var ownPhoto = await AddAssetAsync(MediaAssetPurpose.StaffPhoto, staff.UserId, status);

    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _staffProfileService.UpdateCurrentAsync(staff.UserId, SelfUpdate(photoId: ownPhoto)));
  }

  [Fact]
  public async Task SelfUpdate_UnknownFile_IsRejected()
  {
    var staff = await CreateStaffAsync("staff@example.com");

    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _staffProfileService.UpdateCurrentAsync(staff.UserId, SelfUpdate(photoId: Guid.NewGuid())));
  }

  [Fact]
  public async Task SelfUpdate_OwnReadyPhotoAndQr_AreLinked()
  {
    var staff = await CreateStaffAsync("staff@example.com");
    var ownPhoto = await AddAssetAsync(MediaAssetPurpose.StaffPhoto, staff.UserId);
    var ownQr = await AddAssetAsync(MediaAssetPurpose.TipsQr, staff.UserId);

    var result = await _staffProfileService.UpdateCurrentAsync(staff.UserId, SelfUpdate(ownPhoto, ownQr));

    Assert.Equal(ownPhoto, result.PhotoMediaAssetId);
    Assert.Equal(ownQr, result.TipsQrMediaAssetId);
  }

  [Fact]
  public async Task SelfUpdate_OwnFileAlreadyUsedElsewhere_IsRejected()
  {
    var staff = await CreateStaffAsync("staff@example.com");
    var ownPhoto = await AddAssetAsync(MediaAssetPurpose.StaffPhoto, staff.UserId);
    _dbContext.Banners.Add(new Banner("Promo", 1, DateTime.UtcNow, imageMediaAssetId: ownPhoto));
    await _dbContext.SaveChangesAsync();

    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _staffProfileService.UpdateCurrentAsync(staff.UserId, SelfUpdate(photoId: ownPhoto)));
  }

  [Fact]
  public async Task SelfUpdate_ResendingAFileLinkedBeforeTheFix_KeepsSavingTheForm()
  {
    var staff = await CreateStaffAsync("staff@example.com");
    var customerId = await CreateUserAsync("customer@example.com", RoleNames.Customer);
    var foreign = await AddAssetAsync(MediaAssetPurpose.CustomerReference, customerId);
    await LinkPhotoDirectlyAsync(staff.StaffProfileId, foreign);

    var result = await _staffProfileService.UpdateCurrentAsync(staff.UserId, SelfUpdate(photoId: foreign, bio: "Nueva bio"));

    Assert.Equal(foreign, result.PhotoMediaAssetId);
    Assert.Equal("Nueva bio", result.Bio);
  }

  // ── Linking through the admin form ─────────────────────────────────

  [Fact]
  public async Task AdminUpdate_LinkingAPhotoAnotherProfessionalUses_IsRejected()
  {
    var first = await CreateStaffAsync("first@example.com");
    var second = await CreateStaffAsync("second@example.com");
    var adminId = await CreateUserAsync("admin@example.com", RoleNames.Admin);
    var firstsPhoto = (await _adminStaffService.UploadPhotoAsync(first.StaffProfileId, adminId, Upload())).PhotoMediaAssetId!.Value;

    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _adminStaffService.UpdateAsync(second.StaffProfileId, AdminUpdate(second, photoId: firstsPhoto)));
    await AssertPhotoAsync(second.StaffProfileId, null);
  }

  [Fact]
  public async Task AdminUpdate_LinkingACustomersAttachment_IsRejected()
  {
    var staff = await CreateStaffAsync("staff@example.com");
    var customerId = await CreateUserAsync("customer@example.com", RoleNames.Customer);
    var attachment = await AddAssetAsync(MediaAssetPurpose.CustomerReference, customerId);

    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _adminStaffService.UpdateAsync(staff.StaffProfileId, AdminUpdate(staff, photoId: attachment)));
  }

  [Fact]
  public async Task AdminUpdate_ResendingTheCurrentPhoto_KeepsIt()
  {
    var staff = await CreateStaffAsync("staff@example.com");
    var adminId = await CreateUserAsync("admin@example.com", RoleNames.Admin);
    var photo = (await _adminStaffService.UploadPhotoAsync(staff.StaffProfileId, adminId, Upload())).PhotoMediaAssetId!.Value;

    var result = await _adminStaffService.UpdateAsync(staff.StaffProfileId, AdminUpdate(staff, photoId: photo));

    Assert.Equal(photo, result.PhotoMediaAssetId);
  }

  [Fact]
  public async Task AdminCreate_LinkingAnotherProfessionalsPhoto_IsRejected()
  {
    var victim = await CreateStaffAsync("victim@example.com");
    var adminId = await CreateUserAsync("admin@example.com", RoleNames.Admin);
    var victimPhoto = (await _adminStaffService.UploadPhotoAsync(victim.StaffProfileId, adminId, Upload())).PhotoMediaAssetId!.Value;

    var request = new AdminStaffCreateRequest(
        "New Staff", "new@example.com", "New", "Secret123!", null, null, null, victimPhoto, null, true);

    await Assert.ThrowsAsync<ValidationProblemException>(() => _adminStaffService.CreateAsync(request));
  }

  // ── Removing and replacing ─────────────────────────────────────────

  [Fact]
  public async Task SelfRemovePhoto_OfAFileLinkedBeforeTheFix_OnlyUnlinksIt()
  {
    var staff = await CreateStaffAsync("staff@example.com");
    var customerId = await CreateUserAsync("customer@example.com", RoleNames.Customer);
    var foreign = await AddAssetAsync(MediaAssetPurpose.CustomerReference, customerId);
    await LinkPhotoDirectlyAsync(staff.StaffProfileId, foreign);

    var result = await _staffProfileService.RemovePhotoAsync(staff.UserId);

    Assert.Null(result.PhotoMediaAssetId);
    Assert.Equal(MediaAssetStatus.Ready, await StatusOfAsync(foreign));
    Assert.Empty(_storage.DeletedKeys);
  }

  [Fact]
  public async Task SelfRemoveTipsQr_OfAnotherProfessionalsQr_OnlyUnlinksIt()
  {
    var staff = await CreateStaffAsync("staff@example.com");
    var other = await CreateStaffAsync("other@example.com");
    var othersQr = await AddAssetAsync(MediaAssetPurpose.TipsQr, other.UserId);
    var profile = await _dbContext.StaffProfiles.SingleAsync(candidate => candidate.Id == staff.StaffProfileId);
    profile.SetTipsQr(othersQr, DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();

    await _staffProfileService.RemoveTipsQrAsync(staff.UserId);

    Assert.Equal(MediaAssetStatus.Ready, await StatusOfAsync(othersQr));
    Assert.Empty(_storage.DeletedKeys);
  }

  [Fact]
  public async Task SelfRemovePhoto_OfAFileStillUsedElsewhere_KeepsTheFile()
  {
    var staff = await CreateStaffAsync("staff@example.com");
    var ownPhoto = await AddAssetAsync(MediaAssetPurpose.StaffPhoto, staff.UserId);
    await LinkPhotoDirectlyAsync(staff.StaffProfileId, ownPhoto);
    _dbContext.Banners.Add(new Banner("Promo", 1, DateTime.UtcNow, imageMediaAssetId: ownPhoto));
    await _dbContext.SaveChangesAsync();

    await _staffProfileService.RemovePhotoAsync(staff.UserId);

    Assert.Equal(MediaAssetStatus.Ready, await StatusOfAsync(ownPhoto));
    Assert.Empty(_storage.DeletedKeys);
  }

  [Fact]
  public async Task SelfRemovePhoto_UploadedByAnAdministrator_DeletesIt()
  {
    var staff = await CreateStaffAsync("staff@example.com");
    var adminId = await CreateUserAsync("admin@example.com", RoleNames.Admin);
    var photo = (await _adminStaffService.UploadPhotoAsync(staff.StaffProfileId, adminId, Upload())).PhotoMediaAssetId!.Value;
    var key = await KeyOfAsync(photo);

    await _staffProfileService.RemovePhotoAsync(staff.UserId);

    Assert.Equal(MediaAssetStatus.Archived, await StatusOfAsync(photo));
    Assert.Equal([key], _storage.DeletedKeys);
  }

  [Fact]
  public async Task SelfUploadPhoto_ReplacingTheirOwnPhoto_DeletesTheOldOne()
  {
    var staff = await CreateStaffAsync("staff@example.com");
    var first = (await _staffProfileService.UploadPhotoAsync(staff.UserId, Upload())).PhotoMediaAssetId!.Value;
    var firstKey = await KeyOfAsync(first);

    var second = (await _staffProfileService.UploadPhotoAsync(staff.UserId, Upload())).PhotoMediaAssetId!.Value;

    Assert.NotEqual(first, second);
    Assert.Equal(MediaAssetStatus.Archived, await StatusOfAsync(first));
    Assert.Equal([firstKey], _storage.DeletedKeys);
  }

  [Fact]
  public async Task SelfUploadPhoto_ReplacingAFileLinkedBeforeTheFix_KeepsThatFile()
  {
    var staff = await CreateStaffAsync("staff@example.com");
    var victim = await CreateStaffAsync("victim@example.com");
    var victimPhoto = await AddAssetAsync(MediaAssetPurpose.StaffPhoto, victim.UserId);
    await LinkPhotoDirectlyAsync(victim.StaffProfileId, victimPhoto);
    await LinkPhotoDirectlyAsync(staff.StaffProfileId, victimPhoto);

    var result = await _staffProfileService.UploadPhotoAsync(staff.UserId, Upload());

    Assert.NotEqual(victimPhoto, result.PhotoMediaAssetId);
    Assert.Equal(MediaAssetStatus.Ready, await StatusOfAsync(victimPhoto));
    Assert.Empty(_storage.DeletedKeys);
    await AssertPhotoAsync(victim.StaffProfileId, victimPhoto);
  }

  [Fact]
  public async Task AdminRemoveTipsQr_UploadedByTheProfessional_DeletesIt()
  {
    var staff = await CreateStaffAsync("staff@example.com");
    var qr = (await _staffProfileService.UploadTipsQrAsync(staff.UserId, Upload("qr.png", "image/png"))).TipsQrMediaAssetId!.Value;

    await _adminStaffService.RemoveTipsQrAsync(staff.StaffProfileId);

    Assert.Equal(MediaAssetStatus.Archived, await StatusOfAsync(qr));
    Assert.Single(_storage.DeletedKeys);
  }

  [Fact]
  public async Task AdminUploadPhoto_ReplacingTheProfessionalsOwnPhoto_DeletesTheOldOne()
  {
    var staff = await CreateStaffAsync("staff@example.com");
    var adminId = await CreateUserAsync("admin@example.com", RoleNames.Admin);
    var own = (await _staffProfileService.UploadPhotoAsync(staff.UserId, Upload())).PhotoMediaAssetId!.Value;

    await _adminStaffService.UploadPhotoAsync(staff.StaffProfileId, adminId, Upload());

    Assert.Equal(MediaAssetStatus.Archived, await StatusOfAsync(own));
    Assert.Single(_storage.DeletedKeys);
  }

  [Fact]
  public async Task StorageFailure_WhenReleasingAnOwnedFile_QueuesAPendingDeletion()
  {
    var staff = await CreateStaffAsync("staff@example.com");
    var photo = (await _staffProfileService.UploadPhotoAsync(staff.UserId, Upload())).PhotoMediaAssetId!.Value;
    _storage.FailDeletes = true;

    await _staffProfileService.RemovePhotoAsync(staff.UserId);

    Assert.Equal(MediaAssetStatus.Archived, await StatusOfAsync(photo));
    Assert.Single(await _dbContext.PendingFileDeletions.ToListAsync());
  }

  // ── The customer avatar endpoint is another way to reach the same file ──

  [Fact]
  public async Task CustomerRemovePhoto_WhenTheAvatarPointsToSomeoneElsesFile_OnlyUnlinksIt()
  {
    var staff = await CreateStaffAsync("staff@example.com");
    var customerId = await CreateUserAsync("customer@example.com", RoleNames.Customer);
    var foreign = await AddAssetAsync(MediaAssetPurpose.CustomerReference, customerId);
    var user = await _dbContext.Users.SingleAsync(candidate => candidate.Id == staff.UserId);
    user.SetProfilePhoto(foreign, DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();

    await _customerProfileService.RemovePhotoAsync(staff.UserId);

    Assert.Equal(MediaAssetStatus.Ready, await StatusOfAsync(foreign));
    Assert.Empty(_storage.DeletedKeys);
  }

  [Fact]
  public async Task CustomerRemovePhoto_OfTheirOwnPhoto_DeletesIt()
  {
    var customerId = await CreateUserAsync("customer@example.com", RoleNames.Customer);
    var photo = (await UploadCustomerPhotoAsync(customerId));

    await _customerProfileService.RemovePhotoAsync(customerId);

    Assert.Equal(MediaAssetStatus.Archived, await StatusOfAsync(photo));
    Assert.Single(_storage.DeletedKeys);
  }

  [Fact]
  public async Task CustomerRemovePhoto_OfAStaffPhotoMirroredInTheAvatar_KeepsTheStaffPhoto()
  {
    var staff = await CreateStaffAsync("staff@example.com");
    var photo = (await _staffProfileService.UploadPhotoAsync(staff.UserId, Upload())).PhotoMediaAssetId!.Value;

    await _customerProfileService.RemovePhotoAsync(staff.UserId);

    Assert.Equal(MediaAssetStatus.Ready, await StatusOfAsync(photo));
    Assert.Empty(_storage.DeletedKeys);
    await AssertPhotoAsync(staff.StaffProfileId, photo);
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private async Task<StaffManagementView> CreateStaffAsync(string email)
      => await _adminStaffService.CreateAsync(new AdminStaffCreateRequest(
          "Staff " + email, email, "Staff " + email[..4], "Secret123!", null, null, null, null, null, true));

  private async Task<Guid> CreateUserAsync(string email, string roleName)
  {
    await _seedService.EnsureSeededAsync();
    var now = DateTime.UtcNow;
    var user = new User("User " + email, email, new PasswordHasher<object>().HashPassword(new object(), "Secret123!"), now, null);
    var role = await _dbContext.Roles.SingleAsync(candidate => candidate.NormalizedName == roleName.ToUpperInvariant());
    user.UserRoles.Add(new UserRole(user.Id, role.Id, now));
    _dbContext.Users.Add(user);
    await _dbContext.SaveChangesAsync();
    return user.Id;
  }

  private async Task<Guid> AddAssetAsync(MediaAssetPurpose purpose, Guid uploadedBy, MediaAssetStatus status = MediaAssetStatus.Ready)
  {
    var now = DateTime.UtcNow;
    var asset = new MediaAsset("file.png", "image/png", 10, $"media/{Guid.NewGuid():N}.png", purpose, uploadedBy, now);
    switch (status)
    {
      case MediaAssetStatus.Ready:
        asset.MarkReady(null, now);
        break;
      case MediaAssetStatus.Failed:
        asset.MarkFailed("upload failed", now);
        break;
      case MediaAssetStatus.Archived:
        asset.MarkReady(null, now);
        asset.Archive(now);
        break;
    }

    _dbContext.MediaAssets.Add(asset);
    await _dbContext.SaveChangesAsync();
    return asset.Id;
  }

  /// <summary>Simulates an association made before the fix, bypassing the new checks.</summary>
  private async Task LinkPhotoDirectlyAsync(Guid staffProfileId, Guid mediaAssetId)
  {
    var profile = await _dbContext.StaffProfiles.Include(candidate => candidate.User).SingleAsync(candidate => candidate.Id == staffProfileId);
    profile.SetPhoto(mediaAssetId, DateTime.UtcNow);
    profile.User.SetProfilePhoto(mediaAssetId, DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();
  }

  private async Task<Guid> UploadCustomerPhotoAsync(Guid customerId)
  {
    using var content = new MemoryStream([1, 2, 3]);
    await _customerProfileService.UploadPhotoAsync(
        customerId, new Barbershop.Application.Customer.CustomerPhotoUploadRequest("me.jpg", "image/jpeg", 3, content));
    var user = await _dbContext.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == customerId);
    return user.ProfilePhotoMediaAssetId!.Value;
  }

  private async Task AssertPhotoAsync(Guid staffProfileId, Guid? expected)
  {
    var profile = await _dbContext.StaffProfiles.AsNoTracking().SingleAsync(candidate => candidate.Id == staffProfileId);
    Assert.Equal(expected, profile.PhotoMediaAssetId);
  }

  private async Task<MediaAssetStatus> StatusOfAsync(Guid mediaAssetId)
      => (await _dbContext.MediaAssets.AsNoTracking().SingleAsync(asset => asset.Id == mediaAssetId)).Status;

  private async Task<string> KeyOfAsync(Guid mediaAssetId)
      => (await _dbContext.MediaAssets.AsNoTracking().SingleAsync(asset => asset.Id == mediaAssetId)).StorageKey;

  private static StaffMediaUploadRequest Upload(string fileName = "photo.jpg", string contentType = "image/jpeg")
      => new(fileName, contentType, 3, new MemoryStream([1, 2, 3]));

  private static StaffProfileUpdateRequest SelfUpdate(Guid? photoId = null, Guid? tipsQrId = null, string? bio = null)
      => new("Display Name", bio, null, null, photoId, tipsQrId, null, null, null, null, null);

  private static AdminStaffUpdateRequest AdminUpdate(StaffManagementView staff, Guid? photoId = null)
      => new(staff.FullName, staff.Email, staff.DisplayName, null, null, null, photoId, null, true);

  private sealed class RecordingFileStorageService : IFileStorageService
  {
    public List<string> DeletedKeys { get; } = [];

    public bool FailDeletes { get; set; }

    public Task<StoredFileResult> UploadAsync(FileStorageObject file, CancellationToken cancellationToken = default)
        => Task.FromResult(new StoredFileResult(file.ObjectKey, null));

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
      if (FailDeletes)
      {
        throw new IOException("storage unavailable");
      }

      DeletedKeys.Add(objectKey);
      return Task.CompletedTask;
    }

    public string? GetPublicUrl(string storageKey) => null;
  }

  /// <summary>Stores uploads as ready assets of the requested purpose, uploaded by the caller.</summary>
  private sealed class ReadyMediaAssetsService(AppDbContext dbContext) : IMediaAssetsService
  {
    public async Task<MediaAssetView> UploadAsync(
        Guid currentUserId, IReadOnlyCollection<string> roles, MediaAssetUploadRequest request, CancellationToken cancellationToken = default)
    {
      var now = DateTime.UtcNow;
      var asset = new MediaAsset(request.FileName, request.ContentType, request.SizeBytes, $"media/{Guid.NewGuid():N}", request.Purpose, currentUserId, now);
      asset.MarkReady(null, now);
      dbContext.MediaAssets.Add(asset);
      await dbContext.SaveChangesAsync(cancellationToken);
      return new MediaAssetView(asset.Id, asset.FileName, asset.ContentType, asset.SizeBytes, asset.StorageKey, asset.PublicUrl,
          asset.Purpose, asset.Status, asset.UploadedByUserId, asset.FailureReason, asset.CreatedAt, asset.UpdatedAt);
    }

    public Task<IReadOnlyList<MediaAssetView>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<MediaAssetView> GetByIdAsync(Guid mediaAssetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task DeleteAsync(Guid mediaAssetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  private sealed class TestHostEnvironment : IHostEnvironment
  {
    public string EnvironmentName { get; set; } = "Testing";

    public string ApplicationName { get; set; } = "Barbershop.Tests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(AppContext.BaseDirectory);
  }
}
