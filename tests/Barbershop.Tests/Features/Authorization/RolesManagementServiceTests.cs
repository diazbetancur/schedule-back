using Barbershop.Application.Authorization;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Authorization;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Features.Authorization;

public sealed class RolesManagementServiceTests : IDisposable
{
  private readonly AppDbContext _dbContext;
  private readonly IAdminRolesService _service;

  public RolesManagementServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    _service = new RolesManagementService(_dbContext, TimeProvider.System);
  }

  public void Dispose() => _dbContext.Dispose();

  private async Task<Permission> SeedPermissionAsync(string code = "inventory.view", string description = "Ver existencias, costos y ganancia de productos")
  {
    var permission = new Permission(code, description, DateTime.UtcNow);
    _dbContext.Permissions.Add(permission);
    await _dbContext.SaveChangesAsync();
    return permission;
  }

  [Fact]
  public async Task CreateAsync_CreatesCustomRole_WithRequestedPermissions()
  {
    var permission = await SeedPermissionAsync();

    var view = await _service.CreateAsync(new RoleCreateRequest("Vendedor", [permission.Id]));

    Assert.NotEqual(Guid.Empty, view.Id);
    Assert.Equal("Vendedor", view.Name);
    Assert.False(view.IsSystemRole);
    Assert.Single(view.Permissions);
    Assert.Equal(permission.Code, view.Permissions[0].Code);
  }

  [Fact]
  public async Task CreateAsync_RejectsDuplicateNameCaseInsensitive()
  {
    await _service.CreateAsync(new RoleCreateRequest("Vendedor", []));

    var exception = await Assert.ThrowsAsync<ConflictException>(() =>
        _service.CreateAsync(new RoleCreateRequest("  vendedor  ", [])));

    Assert.Equal("Ya existe un rol con ese nombre.", exception.Message);
  }

  [Fact]
  public async Task CreateAsync_RejectsUnknownPermissionId()
  {
    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(new RoleCreateRequest("Vendedor", [Guid.NewGuid()])));
  }

  [Fact]
  public async Task CreateAsync_RejectsShortName()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(new RoleCreateRequest("V", [])));

    Assert.Contains("name", exception.Errors.Keys);
  }

  [Fact]
  public async Task UpdateAsync_RenamesRole_AndReplacesPermissions()
  {
    var firstPermission = await SeedPermissionAsync("inventory.view", "Ver existencias, costos y ganancia de productos");
    var secondPermission = await SeedPermissionAsync("bills.open", "Abrir cuentas y editar las propias mientras están abiertas");
    var created = await _service.CreateAsync(new RoleCreateRequest("Vendedor", [firstPermission.Id]));

    var updated = await _service.UpdateAsync(created.Id, new RoleUpdateRequest("Vendedor Senior", [secondPermission.Id]));

    Assert.Equal("Vendedor Senior", updated.Name);
    Assert.Single(updated.Permissions);
    Assert.Equal(secondPermission.Code, updated.Permissions[0].Code);
  }

  [Fact]
  public async Task UpdateAsync_OnSystemRole_ThrowsConflict()
  {
    var systemRole = new Role("Admin", isSystemRole: true);
    _dbContext.Roles.Add(systemRole);
    await _dbContext.SaveChangesAsync();

    await Assert.ThrowsAsync<ConflictException>(() =>
        _service.UpdateAsync(systemRole.Id, new RoleUpdateRequest("Administrador", [])));
  }

  [Fact]
  public async Task UpdateAsync_TheStaffRole_ChangesOnlyItsPermissions()
  {
    // ADR-0020 (BUG-091): an administrator can take a default permission away from Staff; its name stays.
    var charge = await SeedPermissionAsync("bills.charge", "Registrar el cobro de una cuenta");
    var open = await SeedPermissionAsync("bills.open", "Abrir cuentas y editar las propias mientras están abiertas");
    var staff = new Role("Staff", isSystemRole: true);
    _dbContext.Roles.Add(staff);
    staff.AddPermission(charge.Id, DateTime.UtcNow);
    staff.AddPermission(open.Id, DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();

    var updated = await _service.UpdateAsync(staff.Id, new RoleUpdateRequest("staff", [open.Id]));

    Assert.Equal("Staff", updated.Name);
    Assert.True(updated.CanEditPermissions);
    Assert.Equal(["bills.open"], updated.Permissions.Select(permission => permission.Code));
    await Assert.ThrowsAsync<ConflictException>(() => _service.UpdateAsync(staff.Id, new RoleUpdateRequest("Barberos", [open.Id])));
  }

  [Fact]
  public async Task ARetiredPermission_IsNeitherListedNorOffered_AndSavingTheRoleKeepsItsRow()
  {
    // ADR-0020 Q16: sales.register leaves the catalog without deleting anything.
    var retired = await SeedPermissionAsync("sales.register", "Registrar ventas");
    var view = await SeedPermissionAsync("inventory.view", "Ver existencias, costos y ganancia de productos");
    var role = new Role("Vendedor");
    role.AddPermission(retired.Id, DateTime.UtcNow);
    role.AddPermission(view.Id, DateTime.UtcNow);
    _dbContext.Roles.Add(role);
    await _dbContext.SaveChangesAsync();

    Assert.DoesNotContain(await _service.GetAllPermissionsAsync(), permission => permission.Code == "sales.register");
    Assert.Equal(["inventory.view"], (await _service.GetAllAsync()).Single(candidate => candidate.Id == role.Id).Permissions.Select(permission => permission.Code));
    await Assert.ThrowsAsync<ValidationProblemException>(() => _service.UpdateAsync(role.Id, new RoleUpdateRequest("Vendedor", [retired.Id])));

    var updated = await _service.UpdateAsync(role.Id, new RoleUpdateRequest("Vendedor", []));

    Assert.Empty(updated.Permissions);
    Assert.Single(await _dbContext.Set<RolePermission>().Where(rolePermission => rolePermission.RoleId == role.Id).ToListAsync());
  }

  [Fact]
  public async Task DeleteAsync_RemovesCustomRole_AndCascadesUserRoles()
  {
    var created = await _service.CreateAsync(new RoleCreateRequest("Vendedor", []));
    var user = new Barbershop.Domain.Users.User("Juan", "juan@example.com", "hash", DateTime.UtcNow);
    user.UserRoles.Add(new UserRole(user.Id, created.Id, DateTime.UtcNow));
    _dbContext.Users.Add(user);
    await _dbContext.SaveChangesAsync();

    await _service.DeleteAsync(created.Id);

    Assert.Empty(await _dbContext.Roles.Where(r => r.Id == created.Id).ToListAsync());
    Assert.Empty(await _dbContext.UserRoles.Where(ur => ur.RoleId == created.Id).ToListAsync());
  }

  [Fact]
  public async Task DeleteAsync_OnSystemRole_ThrowsConflict()
  {
    var systemRole = new Role("Staff", isSystemRole: true);
    _dbContext.Roles.Add(systemRole);
    await _dbContext.SaveChangesAsync();

    await Assert.ThrowsAsync<ConflictException>(() => _service.DeleteAsync(systemRole.Id));
  }

  [Fact]
  public async Task GetAllPermissionsAsync_ReturnsCatalog()
  {
    await SeedPermissionAsync("inventory.view", "Ver existencias, costos y ganancia de productos");

    var permissions = await _service.GetAllPermissionsAsync();

    Assert.Single(permissions);
    Assert.Equal("inventory.view", permissions[0].Code);
  }
}
