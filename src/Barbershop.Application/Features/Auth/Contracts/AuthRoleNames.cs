using Barbershop.Domain.Users;

namespace Barbershop.Application.Auth;

public static class AuthRoleNames
{
    public const string Admin = RoleNames.Admin;
    public const string Staff = RoleNames.Staff;
    public const string Customer = RoleNames.Customer;

    public static IReadOnlyCollection<string> All { get; } = [Admin, Staff, Customer];
}
