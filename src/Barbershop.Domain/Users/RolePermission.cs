using Barbershop.Domain.Common;

namespace Barbershop.Domain.Users;

public sealed class RolePermission
{
  private RolePermission()
  {
  }

  public RolePermission(Guid roleId, Guid permissionId, DateTime assignedAt)
  {
    RoleId = roleId;
    PermissionId = permissionId;
    AssignedAt = DomainValidation.EnsureUtc(assignedAt, nameof(assignedAt));
  }

  public Guid RoleId { get; private set; }
  public Guid PermissionId { get; private set; }
  public DateTime AssignedAt { get; private set; }
  public Role Role { get; private set; } = null!;
  public Permission Permission { get; private set; } = null!;
}
