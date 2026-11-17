using Barbershop.Domain.Common;

namespace Barbershop.Domain.Users;

public sealed class Role
{
  private Role()
  {
  }

  public Role(string name, bool isSystemRole = false)
  {
    SetName(name);
    IsSystemRole = isSystemRole;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public string Name { get; private set; } = string.Empty;
  public string NormalizedName { get; private set; } = string.Empty;
  public bool IsSystemRole { get; private set; }
  public ICollection<UserRole> UserRoles { get; } = [];
  public ICollection<RolePermission> RolePermissions { get; } = [];

  /// <summary>
  /// ADR-0020 (BUG-091): the permissions of the Staff system role can be edited (its name cannot, and it cannot be
  /// deleted); Admin and Customer stay locked, so nobody can lock everyone out.
  /// </summary>
  public bool CanEditPermissions => !IsSystemRole || NormalizedName == DomainValidation.NormalizeKey(RoleNames.Staff);

  public void SetName(string name)
  {
    Name = DomainValidation.Required(name, nameof(name), 50, 2);
    NormalizedName = DomainValidation.NormalizeKey(Name);
  }

  public void AddPermission(Guid permissionId, DateTime assignedAtUtc)
  {
    if (!CanEditPermissions)
    {
      throw new InvalidOperationException("System roles cannot have their permissions modified directly.");
    }

    if (RolePermissions.Any(rp => rp.PermissionId == permissionId))
    {
      return;
    }

    RolePermissions.Add(new RolePermission(Id, permissionId, assignedAtUtc));
  }

  public void RemovePermission(Guid permissionId)
  {
    if (!CanEditPermissions)
    {
      throw new InvalidOperationException("System roles cannot have their permissions modified directly.");
    }

    var existing = RolePermissions.FirstOrDefault(rp => rp.PermissionId == permissionId);
    if (existing is not null)
    {
      RolePermissions.Remove(existing);
    }
  }
}

public static class RoleNames
{
  public const string Admin = "Admin";
  public const string Staff = "Staff";
  public const string Customer = "Customer";

  public static IReadOnlyCollection<string> All { get; } = [Admin, Staff, Customer];
}