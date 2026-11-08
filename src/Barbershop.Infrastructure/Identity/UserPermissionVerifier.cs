using Barbershop.Application.Auth;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Identity;

internal sealed class UserPermissionVerifier : IUserPermissionVerifier
{
    private readonly AppDbContext _dbContext;

    public UserPermissionVerifier(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // One query on every call, never cached: a revocation must be seen by the next critical operation.
    public Task<bool> HasPermissionAsync(Guid userId, string permissionCode, CancellationToken cancellationToken = default)
        => _dbContext.Users
            .AsNoTracking()
            .AnyAsync(
                user => user.Id == userId
                    && user.IsActive
                    && user.UserRoles.Any(userRole => userRole.Role.RolePermissions
                        .Any(rolePermission => rolePermission.Permission.Code == permissionCode)),
                cancellationToken);
}
