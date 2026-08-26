namespace Barbershop.Domain.Users;

public static class PermissionCodes
{
  public const string SalesRegister = "sales.register";

  public static IReadOnlyCollection<string> All { get; } = [SalesRegister];
}
