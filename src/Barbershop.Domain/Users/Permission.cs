using Barbershop.Domain.Common;

namespace Barbershop.Domain.Users;

public sealed class Permission
{
  private Permission()
  {
  }

  public Permission(string code, string description, DateTime createdAt)
  {
    Code = DomainValidation.Required(code, nameof(code), 100, 2);
    Description = DomainValidation.Required(description, nameof(description), 500, 2);
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public string Code { get; private set; } = string.Empty;
  public string Description { get; private set; } = string.Empty;
  public DateTime CreatedAt { get; private set; }
  public ICollection<RolePermission> RolePermissions { get; } = [];
}
