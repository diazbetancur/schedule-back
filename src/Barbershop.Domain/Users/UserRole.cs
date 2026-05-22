using Barbershop.Domain.Common;

namespace Barbershop.Domain.Users;

public sealed class UserRole
{
  private UserRole()
  {
  }

  public UserRole(Guid userId, Guid roleId, DateTime assignedAt)
  {
    UserId = userId;
    RoleId = roleId;
    AssignedAt = DomainValidation.EnsureUtc(assignedAt, nameof(assignedAt));
  }

  public Guid UserId { get; private set; }
  public Guid RoleId { get; private set; }
  public DateTime AssignedAt { get; private set; }
  public User User { get; private set; } = null!;
  public Role Role { get; private set; } = null!;
}