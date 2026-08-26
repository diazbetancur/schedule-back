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

  private async Task<Permission> SeedPermissionAsync(string code = "sales.register", string description = "Registrar ventas")
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

    Assert.Equal("A role with this name already exists.", exception.Message);
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
    var firstPermission = await SeedPermissionAsync("sales.register", "Registrar ventas");
    var secondPermission = await SeedPermissionAsync("sales.report", "Ver reportes de ventas");
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
    await SeedPermissionAsync("sales.register", "Registrar ventas");

    var permissions = await _service.GetAllPermissionsAsync();

    Assert.Single(permissions);
    Assert.Equal("sales.register", permissions[0].Code);
  }
}
