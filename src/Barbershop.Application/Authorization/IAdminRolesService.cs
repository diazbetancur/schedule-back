namespace Barbershop.Application.Authorization;

public interface IAdminRolesService
{
    Task<IReadOnlyList<RoleView>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<RoleView> CreateAsync(RoleCreateRequest request, CancellationToken cancellationToken = default);

    Task<RoleView> UpdateAsync(Guid roleId, RoleUpdateRequest request, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid roleId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PermissionView>> GetAllPermissionsAsync(CancellationToken cancellationToken = default);
}
