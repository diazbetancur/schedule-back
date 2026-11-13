using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Users.Admin;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Users;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Features.Users;

public sealed class AdminUsersServiceTests : IDisposable
{
  private readonly AppDbContext _dbContext;
  private readonly IAdminUsersService _service;

  public AdminUsersServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    _service = new AdminUsersService(_dbContext, TimeProvider.System);
  }

  public void Dispose() => _dbContext.Dispose();

  private async Task<User> CreateUserAsync(string email = "juan@example.com")
  {
    var user = new User("Juan Barbero", email, "hash", DateTime.UtcNow);
    _dbContext.Users.Add(user);
    await _dbContext.SaveChangesAsync();
    return user;
  }

  private async Task<Role> CreateCustomRoleAsync(string name)
  {
    var role = new Role(name);
    _dbContext.Roles.Add(role);
    await _dbContext.SaveChangesAsync();
    return role;
  }

  [Fact]
  public async Task GetAllActiveAsync_ReturnsOnlyActiveUsers_WithEmptyCustomRoleIds_ByDefault()
  {
    await CreateUserAsync();

    var users = await _service.GetAllActiveAsync();

    var user = Assert.Single(users);
    Assert.Empty(user.CustomRoleIds);
  }

  [Fact]
  public async Task GetByIdAsync_UnknownUser_ThrowsNotFound()
  {
    await Assert.ThrowsAsync<NotFoundException>(() => _service.GetByIdAsync(Guid.NewGuid()));
  }

  [Fact]
  public async Task UpdateAsync_ChangesFullNameAndPhone()
  {
    var user = await CreateUserAsync();

    var updated = await _service.UpdateAsync(user.Id, new AdminUserUpdateRequest("Juan Actualizado", "999888777"));

    Assert.Equal("Juan Actualizado", updated.FullName);
    Assert.Equal("999888777", updated.PhoneNumber);
  }

  // ── BUG-076: the platform always keeps an active administrator ──

  [Fact]
  public async Task DeactivateAsync_OwnAccount_IsRejected()
  {
    var admin = await CreateAdminAsync("admin-1@example.com");
    await CreateAdminAsync("admin-2@example.com");
    var service = ServiceActingAs(admin.Id);

    var exception = await Assert.ThrowsAsync<ConflictException>(() => service.DeactivateAsync(admin.Id));

    Assert.Contains("tu propia cuenta", exception.Message);
    Assert.True((await _dbContext.Users.AsNoTracking().SingleAsync(u => u.Id == admin.Id)).IsActive);
  }

  [Fact]
  public async Task DeactivateAsync_TheLastActiveAdmin_IsRejected_EvenWithInactiveAdminsAround()
  {
    var lastAdmin = await CreateAdminAsync("last@example.com");
    var formerAdmin = await CreateAdminAsync("former@example.com");
    formerAdmin.Deactivate(DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();
    var actingUser = await CreateUserAsync("operator@example.com");

    var exception = await Assert.ThrowsAsync<ConflictException>(
        () => ServiceActingAs(actingUser.Id).DeactivateAsync(lastAdmin.Id));

    Assert.Contains("último administrador activo", exception.Message);
    Assert.True((await _dbContext.Users.AsNoTracking().SingleAsync(u => u.Id == lastAdmin.Id)).IsActive);
  }

  [Fact]
  public async Task DeactivateAsync_AnotherAdmin_WhenOneRemainsActive_IsAllowed()
  {
    var actingAdmin = await CreateAdminAsync("admin-1@example.com");
    var otherAdmin = await CreateAdminAsync("admin-2@example.com");

    await ServiceActingAs(actingAdmin.Id).DeactivateAsync(otherAdmin.Id);

    Assert.False((await _dbContext.Users.AsNoTracking().SingleAsync(u => u.Id == otherAdmin.Id)).IsActive);
  }

  [Fact]
  public async Task DeactivateAsync_ANonAdmin_IsAllowed_WhileASingleAdminExists()
  {
    var admin = await CreateAdminAsync("admin@example.com");
    var customer = await CreateUserAsync("customer@example.com");

    await ServiceActingAs(admin.Id).DeactivateAsync(customer.Id);

    Assert.False((await _dbContext.Users.AsNoTracking().SingleAsync(u => u.Id == customer.Id)).IsActive);
  }

  private IAdminUsersService ServiceActingAs(Guid userId)
      => new AdminUsersService(_dbContext, TimeProvider.System, new FixedCurrentUser(userId));

  private async Task<User> CreateAdminAsync(string email)
  {
    var adminRole = await _dbContext.Roles.SingleOrDefaultAsync(r => r.NormalizedName == "ADMIN")
        ?? _dbContext.Roles.Add(new Role(RoleNames.Admin, isSystemRole: true)).Entity;
    var user = new User("Admin", email, "hash", DateTime.UtcNow);
    user.UserRoles.Add(new UserRole(user.Id, adminRole.Id, DateTime.UtcNow));
    _dbContext.Users.Add(user);
    await _dbContext.SaveChangesAsync();
    return user;
  }

  private sealed class FixedCurrentUser(Guid userId) : Barbershop.Application.Auth.ICurrentUserAccessor
  {
    public Guid? UserId { get; } = userId;
  }

  [Fact]
  public async Task DeactivateAsync_MarksUserInactive()
  {
    var user = await CreateUserAsync();

    await _service.DeactivateAsync(user.Id);

    var stored = await _dbContext.Users.SingleAsync(u => u.Id == user.Id);
    Assert.False(stored.IsActive);
  }

  [Fact]
  public async Task UpdateCustomRolesAsync_AssignsCustomRoles_AndReportsThemInCustomRoleIds()
  {
    var user = await CreateUserAsync();
    var sellerRole = await CreateCustomRoleAsync("Vendedor");

    var updated = await _service.UpdateCustomRolesAsync(user.Id, new AdminUserRolesUpdateRequest([sellerRole.Id]));

    Assert.Contains(sellerRole.Id, updated.CustomRoleIds);
    Assert.Contains("Vendedor", updated.Roles);
  }

  [Fact]
  public async Task UpdateCustomRolesAsync_ReplacesPreviousCustomRoles_ButKeepsSystemRoles()
  {
    var user = await CreateUserAsync();
    var customerRole = new Role(RoleNames.Customer, isSystemRole: true);
    _dbContext.Roles.Add(customerRole);
    await _dbContext.SaveChangesAsync();
    user.UserRoles.Add(new UserRole(user.Id, customerRole.Id, DateTime.UtcNow));
    var sellerRole = await CreateCustomRoleAsync("Vendedor");
    var supportRole = await CreateCustomRoleAsync("Soporte");
    await _service.UpdateCustomRolesAsync(user.Id, new AdminUserRolesUpdateRequest([sellerRole.Id]));

    var updated = await _service.UpdateCustomRolesAsync(user.Id, new AdminUserRolesUpdateRequest([supportRole.Id]));

    Assert.DoesNotContain(sellerRole.Id, updated.CustomRoleIds);
    Assert.Contains(supportRole.Id, updated.CustomRoleIds);
    Assert.Contains("Customer", updated.Roles);
  }

  [Fact]
  public async Task UpdateCustomRolesAsync_RejectsSystemRoleId()
  {
    var user = await CreateUserAsync();
    var adminRole = new Role(RoleNames.Admin, isSystemRole: true);
    _dbContext.Roles.Add(adminRole);
    await _dbContext.SaveChangesAsync();

    await Assert.ThrowsAsync<ArgumentException>(() =>
        _service.UpdateCustomRolesAsync(user.Id, new AdminUserRolesUpdateRequest([adminRole.Id])));
  }

  [Fact]
  public async Task UpdateCustomRolesAsync_RejectsUnknownRoleId()
  {
    var user = await CreateUserAsync();

    await Assert.ThrowsAsync<ArgumentException>(() =>
        _service.UpdateCustomRolesAsync(user.Id, new AdminUserRolesUpdateRequest([Guid.NewGuid()])));
  }

  [Fact]
  public async Task UpdateCustomRolesAsync_RoleKeptAcrossCalls_PreservesAssignedAt()
  {
    var user = await CreateUserAsync();
    var sellerRole = await CreateCustomRoleAsync("Vendedor");
    var supportRole = await CreateCustomRoleAsync("Soporte");

    await _service.UpdateCustomRolesAsync(user.Id, new AdminUserRolesUpdateRequest([sellerRole.Id]));
    var firstAssignedAt = await _dbContext.UserRoles
        .Where(ur => ur.UserId == user.Id && ur.RoleId == sellerRole.Id)
        .Select(ur => ur.AssignedAt)
        .SingleAsync();

    await _service.UpdateCustomRolesAsync(user.Id, new AdminUserRolesUpdateRequest([sellerRole.Id, supportRole.Id]));
    var secondAssignedAt = await _dbContext.UserRoles
        .Where(ur => ur.UserId == user.Id && ur.RoleId == sellerRole.Id)
        .Select(ur => ur.AssignedAt)
        .SingleAsync();

    Assert.Equal(firstAssignedAt, secondAssignedAt);
  }
}
