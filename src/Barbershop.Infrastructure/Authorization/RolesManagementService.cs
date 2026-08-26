using Barbershop.Application.Authorization;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Authorization;

internal sealed class RolesManagementService : IAdminRolesService
{
    private const int NameMinLength = 2;
    private const int NameMaxLength = 50;

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public RolesManagementService(AppDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<RoleView>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var roles = await _dbContext.Roles
            .Include(role => role.RolePermissions)
            .ThenInclude(rolePermission => rolePermission.Permission)
            .OrderBy(role => role.Name)
            .ToListAsync(cancellationToken);

        return roles.Select(Map).ToArray();
    }

    public async Task<RoleView> CreateAsync(RoleCreateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateName(request.Name);
        await EnsureNameIsUniqueAsync(request.Name, null, cancellationToken);
        var permissions = await LoadPermissionsAsync(request.PermissionIds, cancellationToken);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var role = new Role(request.Name);
        foreach (var permission in permissions)
        {
            role.AddPermission(permission.Id, utcNow);
        }

        _dbContext.Roles.Add(role);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Map(role);
    }

    public async Task<RoleView> UpdateAsync(Guid roleId, RoleUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var role = await LoadAsync(roleId, cancellationToken);
        if (role.IsSystemRole)
        {
            throw new ConflictException("System roles cannot be edited.");
        }

        ValidateName(request.Name);
        await EnsureNameIsUniqueAsync(request.Name, roleId, cancellationToken);
        var permissions = await LoadPermissionsAsync(request.PermissionIds, cancellationToken);

        role.SetName(request.Name);

        var targetPermissionIds = permissions.Select(permission => permission.Id).ToHashSet();
        var currentPermissionIds = role.RolePermissions.Select(rolePermission => rolePermission.PermissionId).ToHashSet();

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var permissionId in currentPermissionIds.Except(targetPermissionIds))
        {
            role.RemovePermission(permissionId);
        }

        foreach (var permissionId in targetPermissionIds.Except(currentPermissionIds))
        {
            role.AddPermission(permissionId, utcNow);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Map(role);
    }

    public async Task DeleteAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        var role = await LoadAsync(roleId, cancellationToken);
        if (role.IsSystemRole)
        {
            throw new ConflictException("System roles cannot be deleted.");
        }

        _dbContext.Roles.Remove(role);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PermissionView>> GetAllPermissionsAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Permissions
            .OrderBy(permission => permission.Code)
            .Select(permission => new PermissionView(permission.Id, permission.Code, permission.Description))
            .ToListAsync(cancellationToken);
    }

    private async Task<Role> LoadAsync(Guid roleId, CancellationToken cancellationToken)
    {
        return await _dbContext.Roles
            .Include(role => role.RolePermissions)
            .ThenInclude(rolePermission => rolePermission.Permission)
            .SingleOrDefaultAsync(role => role.Id == roleId, cancellationToken)
            ?? throw new NotFoundException("The role was not found.");
    }

    private async Task<List<Permission>> LoadPermissionsAsync(IReadOnlyList<Guid> permissionIds, CancellationToken cancellationToken)
    {
        var uniqueIds = (permissionIds ?? []).Distinct().ToArray();
        if (uniqueIds.Length == 0)
        {
            return [];
        }

        var permissions = await _dbContext.Permissions
            .Where(permission => uniqueIds.Contains(permission.Id))
            .ToListAsync(cancellationToken);

        if (permissions.Count != uniqueIds.Length)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["permissionIds"] = ["One or more permission ids do not exist."]
            });
        }

        return permissions;
    }

    private async Task EnsureNameIsUniqueAsync(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        var normalized = name.Trim().ToUpperInvariant();
        var duplicateExists = await _dbContext.Roles.AnyAsync(
            role => role.Id != excludeId && role.NormalizedName == normalized,
            cancellationToken);

        if (duplicateExists)
        {
            throw new ConflictException("A role with this name already exists.");
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length is < NameMinLength or > NameMaxLength)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["name"] = [$"Name must be between {NameMinLength} and {NameMaxLength} characters."]
            });
        }
    }

    private static RoleView Map(Role role)
        => new(
            role.Id,
            role.Name,
            role.IsSystemRole,
            role.RolePermissions
                .Select(rolePermission => new PermissionView(rolePermission.Permission.Id, rolePermission.Permission.Code, rolePermission.Permission.Description))
                .OrderBy(permission => permission.Code, StringComparer.Ordinal)
                .ToArray());
}
