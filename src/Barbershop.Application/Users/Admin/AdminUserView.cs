namespace Barbershop.Application.Users.Admin;

public sealed record AdminUserListItem(
    Guid UserId,
    string FullName,
    string Email,
    string? PhoneNumber,
    IReadOnlyList<string> Roles,
    bool IsActive,
    DateTime CreatedAt);

public sealed record AdminUserUpdateRequest(
    string FullName,
    string? PhoneNumber);
