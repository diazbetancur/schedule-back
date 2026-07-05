using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Staff.Admin;
using Barbershop.Application.Staff.SelfService;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Configuration;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Staff;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Barbershop.Tests.Features.Staff;

public sealed class StaffManagementServiceTests : IDisposable
{
  private readonly AppDbContext _dbContext;
  private readonly IAdminStaffService _adminStaffService;
  private readonly IStaffProfileService _staffProfileService;
  private readonly IdentitySeedService _seedService;

  public StaffManagementServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);

    var passwordHasher = new PasswordHasher<object>();
    var hostEnvironment = new TestHostEnvironment();
    var timeProvider = TimeProvider.System;
    var seedService = new IdentitySeedService(
        _dbContext,
        passwordHasher,
        Options.Create(new SeedAdminOptions()),
        hostEnvironment,
        timeProvider);
    _seedService = seedService;

    var staffManagementService = new StaffManagementService(
        _dbContext,
        seedService,
        passwordHasher,
        timeProvider,
        null!,
        null!);

    _adminStaffService = staffManagementService;
    _staffProfileService = staffManagementService;
  }

  public void Dispose()
  {
    _dbContext.Dispose();
  }

  [Fact]
  public async Task GetAllAsync_ReturnsCoreStaffProfileData()
  {
    var first = await CreateStaffAsync("alex@example.com", "Alex Diaz", "Alex");
    var second = await CreateStaffAsync("bianca@example.com", "Bianca Mendez", "Bianca");

    var staff = await _adminStaffService.GetAllAsync();

    Assert.Equal(2, staff.Count);
    Assert.Contains(staff, item => item.StaffProfileId == first.StaffProfileId
        && item.UserId == first.UserId
        && item.FullName == "Alex Diaz"
        && item.Email == "alex@example.com"
        && item.DisplayName == "Alex");
    Assert.Contains(staff, item => item.StaffProfileId == second.StaffProfileId
        && item.DisplayName == "Bianca");
  }

  [Fact]
  public async Task CreateAsync_CreatesStaffUserAndProfile()
  {
    var response = await _adminStaffService.CreateAsync(CreateRequest("alex@example.com", "Alex Diaz", "Alex"));

    Assert.NotEqual(Guid.Empty, response.StaffProfileId);
    Assert.NotEqual(Guid.Empty, response.UserId);
    Assert.Equal("Alex Diaz", response.FullName);
    Assert.Equal("alex@example.com", response.Email);
    Assert.Equal("Alex", response.DisplayName);
    Assert.Equal(30, response.DefaultAppointmentDurationMinutes);
    Assert.True(response.IsActive);
    Assert.Single(await _dbContext.StaffProfiles.ToListAsync());
  }

  [Fact]
  public async Task CreateAsync_AssignsStaffRole()
  {
    var response = await _adminStaffService.CreateAsync(CreateRequest("role-check@example.com", "Role Check", "Role Check"));

    var user = await _dbContext.Users
        .Include(candidate => candidate.UserRoles)
        .ThenInclude(userRole => userRole.Role)
        .SingleAsync(candidate => candidate.Id == response.UserId);

    Assert.Contains(user.UserRoles, assignment => assignment.Role.Name == RoleNames.Staff);
  }

  [Fact]
  public async Task CreateAsync_RejectsDuplicateEmail()
  {
    await _adminStaffService.CreateAsync(CreateRequest("duplicate@example.com", "First Staff", "First"));

    var exception = await Assert.ThrowsAsync<ConflictException>(() =>
        _adminStaffService.CreateAsync(CreateRequest("DUPLICATE@example.com", "Second Staff", "Second")));

    Assert.Equal("A user with this email already exists.", exception.Message);
  }

  [Fact]
  public async Task UpdateAsync_UpdatesStaffUserAndProfile()
  {
    var created = await _adminStaffService.CreateAsync(CreateRequest("update@example.com", "Original Name", "Original"));

    var updated = await _adminStaffService.UpdateAsync(
        created.StaffProfileId,
        new AdminStaffUpdateRequest(
            "Updated Name",
            "updated@example.com",
            "Updated Display",
            "+5491100000000",
            "Updated bio",
            45,
            null,
            null,
            true));

    Assert.Equal("Updated Name", updated.FullName);
    Assert.Equal("updated@example.com", updated.Email);
    Assert.Equal("Updated Display", updated.DisplayName);
    Assert.Equal("Updated bio", updated.Bio);
    Assert.Equal("+5491100000000", updated.PhoneNumber);
    Assert.Equal(45, updated.DefaultAppointmentDurationMinutes);

    var user = await _dbContext.Users.SingleAsync(candidate => candidate.Id == created.UserId);
    Assert.Equal("UPDATED@EXAMPLE.COM", user.NormalizedEmail);
  }

  [Fact]
  public async Task UpdateStatusAsync_DeactivatesStaffProfileAndUser()
  {
    var created = await _adminStaffService.CreateAsync(CreateRequest("inactive@example.com", "Inactive Staff", "Inactive"));

    var updated = await _adminStaffService.UpdateStatusAsync(created.StaffProfileId, new StaffStatusUpdateRequest(false));

    Assert.False(updated.IsActive);

    var user = await _dbContext.Users.SingleAsync(candidate => candidate.Id == created.UserId);
    var staffProfile = await _dbContext.StaffProfiles.SingleAsync(candidate => candidate.Id == created.StaffProfileId);

    Assert.False(user.IsActive);
    Assert.False(staffProfile.IsActive);
  }

  [Fact]
  public async Task GetCurrentAsync_ReturnsOwnStaffProfile()
  {
    var created = await _adminStaffService.CreateAsync(CreateRequest("self@example.com", "Self Staff", "Self"));

    var current = await _staffProfileService.GetCurrentAsync(created.UserId);

    Assert.Equal(created.StaffProfileId, current.StaffProfileId);
    Assert.Equal(created.UserId, current.UserId);
    Assert.Equal("Self Staff", current.FullName);
    Assert.Equal("self@example.com", current.Email);
    Assert.Equal("Self", current.DisplayName);
  }

  [Fact]
  public async Task UpdateCurrentAsync_OnlyMutatesCurrentStaffProfile()
  {
    var first = await _adminStaffService.CreateAsync(CreateRequest("first@example.com", "First Staff", "First"));
    var second = await _adminStaffService.CreateAsync(CreateRequest("second@example.com", "Second Staff", "Second"));

    var updated = await _staffProfileService.UpdateCurrentAsync(
        first.UserId,
        new StaffProfileUpdateRequest(
            "First Updated",
            "Bio updated by self-service",
            "+5491199999999",
            60,
            null,
            null,
            null,
            null,
            null,
            null,
            null));

    var untouched = await _adminStaffService.GetByIdAsync(second.StaffProfileId);

    Assert.Equal("First Updated", updated.DisplayName);
    Assert.Equal("Bio updated by self-service", updated.Bio);
    Assert.Equal("+5491199999999", updated.PhoneNumber);
    Assert.Equal(60, updated.DefaultAppointmentDurationMinutes);
    Assert.Equal("Second", untouched.DisplayName);
    Assert.Equal("second@example.com", untouched.Email);
  }

  [Fact]
  public async Task CreateAsync_RejectsInvalidDuration()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _adminStaffService.CreateAsync(new AdminStaffCreateRequest(
            "Invalid Duration",
            "duration@example.com",
            "Duration",
            "Secret123!",
            null,
            null,
            241,
            null,
            null,
            true)));

    Assert.Contains("defaultAppointmentDurationMinutes", exception.Errors.Keys);
  }

  [Fact]
  public async Task EnableProfessionalForCurrentUserAsync_AddsStaffRoleAndActiveProfile()
  {
    var adminUserId = await CreateAdminUserAsync("owner@example.com", "Owner Admin");

    var response = await _adminStaffService.EnableProfessionalForCurrentUserAsync(
        adminUserId,
        new EnableProfessionalProfileRequest("Owner", null));

    Assert.Equal(adminUserId, response.UserId);
    Assert.Equal("Owner", response.DisplayName);
    Assert.Equal(30, response.DefaultAppointmentDurationMinutes);
    Assert.True(response.IsActive);

    var user = await _dbContext.Users
        .Include(candidate => candidate.UserRoles)
        .ThenInclude(userRole => userRole.Role)
        .SingleAsync(candidate => candidate.Id == adminUserId);

    Assert.Contains(user.UserRoles, assignment => assignment.Role.Name == RoleNames.Staff);
    Assert.Contains(user.UserRoles, assignment => assignment.Role.Name == RoleNames.Admin);
    Assert.Single(await _dbContext.StaffProfiles.Where(profile => profile.UserId == adminUserId).ToListAsync());
  }

  [Fact]
  public async Task EnableProfessionalForCurrentUserAsync_RejectsWhenAlreadyActive()
  {
    var adminUserId = await CreateAdminUserAsync("dup@example.com", "Dup Admin");
    await _adminStaffService.EnableProfessionalForCurrentUserAsync(
        adminUserId, new EnableProfessionalProfileRequest("Dup", null));

    var exception = await Assert.ThrowsAsync<ConflictException>(() =>
        _adminStaffService.EnableProfessionalForCurrentUserAsync(
            adminUserId, new EnableProfessionalProfileRequest("Dup", null)));

    Assert.Equal("The professional profile is already active.", exception.Message);
  }

  private static AdminStaffCreateRequest CreateRequest(string email, string fullName, string displayName)
      => new(
          fullName,
          email,
          displayName,
          "Secret123!",
          "+5491100000000",
          null,
          null,
          null,
          null,
          true);

  private async Task<Barbershop.Application.Staff.StaffManagementView> CreateStaffAsync(string email, string fullName, string displayName)
      => await _adminStaffService.CreateAsync(CreateRequest(email, fullName, displayName));

  private async Task<Guid> CreateAdminUserAsync(string email, string fullName)
  {
    await _seedService.EnsureSeededAsync();

    var passwordHasher = new PasswordHasher<object>();
    var utcNow = DateTime.UtcNow;
    var user = new User(fullName, email, passwordHasher.HashPassword(new object(), "Secret123!"), utcNow, null);
    var adminRole = await _dbContext.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Admin.ToUpperInvariant());
    user.UserRoles.Add(new UserRole(user.Id, adminRole.Id, utcNow));

    _dbContext.Users.Add(user);
    await _dbContext.SaveChangesAsync();
    return user.Id;
  }

  private sealed class TestHostEnvironment : IHostEnvironment
  {
    public string EnvironmentName { get; set; } = "Testing";

    public string ApplicationName { get; set; } = "Barbershop.Tests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(AppContext.BaseDirectory);
  }
}
