namespace Barbershop.Application.Users.Admin;

public interface IAdminUsersService
{
    Task<IReadOnlyList<AdminUserListItem>> GetAllActiveAsync(CancellationToken cancellationToken = default);
    Task<AdminUserListItem> GetByIdAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<AdminUserListItem> UpdateAsync(Guid userId, AdminUserUpdateRequest request, CancellationToken cancellationToken = default);
    Task DeactivateAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<AdminUserListItem> UpdateCustomRolesAsync(Guid userId, AdminUserRolesUpdateRequest request, CancellationToken cancellationToken = default);
}
