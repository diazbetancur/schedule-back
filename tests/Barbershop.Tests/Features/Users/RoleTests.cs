using Barbershop.Domain.Users;

namespace Barbershop.Tests.Features.Users;

public sealed class RoleTests
{
  [Fact]
  public void Constructor_DefaultsIsSystemRole_ToFalse()
  {
    var role = new Role("Vendedor");

    Assert.False(role.IsSystemRole);
  }

  [Fact]
  public void Constructor_AllowsExplicitSystemRole()
  {
    var role = new Role("Admin", isSystemRole: true);

    Assert.True(role.IsSystemRole);
  }

  [Fact]
  public void AddPermission_OnCustomRole_AddsRolePermission()
  {
    var role = new Role("Vendedor");
    var permissionId = Guid.NewGuid();
    var now = DateTime.UtcNow;

    role.AddPermission(permissionId, now);

    var added = Assert.Single(role.RolePermissions);
    Assert.Equal(permissionId, added.PermissionId);
    Assert.Equal(role.Id, added.RoleId);
  }

  [Fact]
  public void AddPermission_Twice_DoesNotDuplicate()
  {
    var role = new Role("Vendedor");
    var permissionId = Guid.NewGuid();
    var now = DateTime.UtcNow;

    role.AddPermission(permissionId, now);
    role.AddPermission(permissionId, now);

    Assert.Single(role.RolePermissions);
  }

  [Fact]
  public void AddPermission_OnSystemRole_Throws()
  {
    var role = new Role("Admin", isSystemRole: true);

    Assert.Throws<InvalidOperationException>(() => role.AddPermission(Guid.NewGuid(), DateTime.UtcNow));
  }

  [Fact]
  public void RemovePermission_OnCustomRole_RemovesIfPresent()
  {
    var role = new Role("Vendedor");
    var permissionId = Guid.NewGuid();
    role.AddPermission(permissionId, DateTime.UtcNow);

    role.RemovePermission(permissionId);

    Assert.Empty(role.RolePermissions);
  }

  [Fact]
  public void RemovePermission_OnSystemRole_Throws()
  {
    var role = new Role("Staff", isSystemRole: true);

    Assert.Throws<InvalidOperationException>(() => role.RemovePermission(Guid.NewGuid()));
  }
}
