using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Features.Authorization;

public sealed class UserPermissionVerifierTests : IDisposable
{
  private readonly AppDbContext _dbContext;
  private readonly UserPermissionVerifier _verifier;
  private readonly Permission _approve;
  private readonly Permission _deliver;

  public UserPermissionVerifierTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    _verifier = new UserPermissionVerifier(_dbContext);

    _approve = new Permission(PermissionCodes.LoyaltyRedemptionsApprove, "Aprobar redenciones", DateTime.UtcNow);
    _deliver = new Permission(PermissionCodes.LoyaltyRedemptionsDeliver, "Entregar redenciones", DateTime.UtcNow);
    _dbContext.Permissions.AddRange(_approve, _deliver);
    _dbContext.SaveChanges();
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task ActiveUser_WithThePermissionThroughARole_IsGranted()
  {
    var role = await CreateRoleAsync("Aprobador", _approve);
    var user = await CreateUserAsync(role);

    Assert.True(await _verifier.HasPermissionAsync(user.Id, PermissionCodes.LoyaltyRedemptionsApprove));
    Assert.False(await _verifier.HasPermissionAsync(user.Id, PermissionCodes.LoyaltyRedemptionsDeliver));
  }

  [Fact]
  public async Task ThePermission_CanComeFromAnyOfTheUsersRoles()
  {
    var approver = await CreateRoleAsync("Aprobador", _approve);
    var deliverer = await CreateRoleAsync("Entregador", _deliver);
    var user = await CreateUserAsync(approver, deliverer);

    Assert.True(await _verifier.HasPermissionAsync(user.Id, PermissionCodes.LoyaltyRedemptionsApprove));
    Assert.True(await _verifier.HasPermissionAsync(user.Id, PermissionCodes.LoyaltyRedemptionsDeliver));
  }

  [Fact]
  public async Task PermissionRemovedFromTheRole_IsDeniedOnTheNextCheck()
  {
    var role = await CreateRoleAsync("Aprobador", _approve);
    var user = await CreateUserAsync(role);
    Assert.True(await _verifier.HasPermissionAsync(user.Id, PermissionCodes.LoyaltyRedemptionsApprove));

    role.RemovePermission(_approve.Id);
    await _dbContext.SaveChangesAsync();

    Assert.False(await _verifier.HasPermissionAsync(user.Id, PermissionCodes.LoyaltyRedemptionsApprove));
  }

  [Fact]
  public async Task RoleRemovedFromTheUser_IsDeniedOnTheNextCheck()
  {
    var role = await CreateRoleAsync("Aprobador", _approve);
    var user = await CreateUserAsync(role);
    Assert.True(await _verifier.HasPermissionAsync(user.Id, PermissionCodes.LoyaltyRedemptionsApprove));

    user.UserRoles.Remove(user.UserRoles.Single());
    await _dbContext.SaveChangesAsync();

    Assert.False(await _verifier.HasPermissionAsync(user.Id, PermissionCodes.LoyaltyRedemptionsApprove));
  }

  [Fact]
  public async Task InactiveUser_IsDenied_AndGrantedAgainOnceReactivated()
  {
    var role = await CreateRoleAsync("Aprobador", _approve);
    var user = await CreateUserAsync(role);

    user.Deactivate(DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();
    Assert.False(await _verifier.HasPermissionAsync(user.Id, PermissionCodes.LoyaltyRedemptionsApprove));

    user.Activate(DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();
    Assert.True(await _verifier.HasPermissionAsync(user.Id, PermissionCodes.LoyaltyRedemptionsApprove));
  }

  [Fact]
  public async Task UnknownUserOrPermission_IsDenied()
  {
    var role = await CreateRoleAsync("Aprobador", _approve);
    var user = await CreateUserAsync(role);

    Assert.False(await _verifier.HasPermissionAsync(Guid.NewGuid(), PermissionCodes.LoyaltyRedemptionsApprove));
    Assert.False(await _verifier.HasPermissionAsync(user.Id, "unknown.permission"));
  }

  private async Task<Role> CreateRoleAsync(string name, params Permission[] permissions)
  {
    var role = new Role(name);
    foreach (var permission in permissions)
    {
      role.AddPermission(permission.Id, DateTime.UtcNow);
    }

    _dbContext.Roles.Add(role);
    await _dbContext.SaveChangesAsync();
    return role;
  }

  private async Task<User> CreateUserAsync(params Role[] roles)
  {
    var user = new User("Usuario de prueba", $"user-{Guid.NewGuid():N}@example.com", "not-a-real-hash", DateTime.UtcNow);
    foreach (var role in roles)
    {
      user.UserRoles.Add(new UserRole(user.Id, role.Id, DateTime.UtcNow));
    }

    _dbContext.Users.Add(user);
    await _dbContext.SaveChangesAsync();
    return user;
  }
}
