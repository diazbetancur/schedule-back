using Barbershop.Domain.Common;

namespace Barbershop.Domain.Users;

public sealed class Role
{
  private Role()
  {
  }

  public Role(string name)
  {
    SetName(name);
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public string Name { get; private set; } = string.Empty;
  public string NormalizedName { get; private set; } = string.Empty;
  public ICollection<UserRole> UserRoles { get; } = [];

  public void SetName(string name)
  {
    Name = DomainValidation.Required(name, nameof(name), 50, 2);
    NormalizedName = DomainValidation.NormalizeKey(Name);
  }
}

public static class RoleNames
{
  public const string Admin = "Admin";
  public const string Staff = "Staff";
  public const string Customer = "Customer";

  public static IReadOnlyCollection<string> All { get; } = [Admin, Staff, Customer];
}