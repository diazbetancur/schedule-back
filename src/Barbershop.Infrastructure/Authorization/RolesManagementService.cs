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

        var versions = await LoadVersionsAsync(cancellationToken);
        return roles.Select(role => Map(role, versions.GetValueOrDefault(role.Id))).ToArray();
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

    internal const string InvalidVersionMessage = "La versión del rol no es válida. Recarga la página y vuelve a guardar.";

    internal const string ConcurrentChangeMessage =
        "Otra persona cambió este rol mientras lo editabas. Recarga la página para ver sus permisos actuales y vuelve a guardar.";

    public async Task<RoleView> UpdateAsync(Guid roleId, RoleUpdateRequest request, CancellationToken cancellationToken = default)
    {
        // SONDA BUG-161: two saves of the same role at once used to leave a mix of both permission sets. The role row is
        // locked, so the saves run one after the other, and a save based on an older version than the current one is a 409.
        // SONDA BUG-173: a version that is not a version at all («abc») is a field error, not a concurrency conflict.
        if (request.Version is { } sent && !uint.TryParse(sent, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["version"] = [InvalidVersionMessage]
            });
        }

        var relational = _dbContext.Database.IsNpgsql();
        await using var transaction = relational ? await _dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        if (relational)
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.roles WHERE \"Id\" = {roleId} FOR UPDATE", cancellationToken);
            var current = (await LoadVersionsAsync(cancellationToken)).GetValueOrDefault(roleId);
            if (request.Version is { } expected && current is not null && !string.Equals(expected, current, StringComparison.Ordinal))
            {
                throw new ConflictException(ConcurrentChangeMessage);
            }
        }

        var role = await LoadAsync(roleId, cancellationToken);
        if (!role.CanEditPermissions)
        {
            throw new ConflictException("Los roles del sistema no se pueden editar.");
        }

        ValidateName(request.Name);
        if (role.IsSystemRole)
        {
            // ADR-0020 (BUG-091): only the permissions of Staff change; its name is fixed, like the role itself.
            if (!string.Equals(request.Name.Trim(), role.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ConflictException("El nombre de un rol del sistema no se puede cambiar; solo sus permisos.");
            }
        }
        else
        {
            await EnsureNameIsUniqueAsync(request.Name, roleId, cancellationToken);
            role.SetName(request.Name);
        }

        var permissions = await LoadPermissionsAsync(request.PermissionIds, cancellationToken);

        // Retired permissions (ADR-0020 Q16) are neither shown nor sent back: they stay exactly as they are.
        var targetPermissionIds = permissions.Select(permission => permission.Id).ToHashSet();
        var currentPermissionIds = role.RolePermissions
            .Where(rolePermission => PermissionCodes.IsInCatalog(rolePermission.Permission.Code))
            .Select(rolePermission => rolePermission.PermissionId)
            .ToHashSet();

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
        if (transaction is not null)
        {
            // The permissions live in role_permissions: touching the role row gives the role a new version every save.
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.roles SET \"Name\" = \"Name\" WHERE \"Id\" = {roleId}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return Map(role, (await LoadVersionsAsync(cancellationToken)).GetValueOrDefault(roleId));
    }

    /// <summary>The PostgreSQL row version (xmin) of each role; empty with other providers.</summary>
    private async Task<Dictionary<Guid, string>> LoadVersionsAsync(CancellationToken cancellationToken)
        => _dbContext.Database.IsNpgsql()
            ? await _dbContext.Database
                .SqlQueryRaw<RoleVersionRow>("SELECT \"Id\", xmin::text AS \"Version\" FROM public.roles")
                .ToDictionaryAsync(row => row.Id, row => row.Version, cancellationToken)
            : [];

    private sealed record RoleVersionRow(Guid Id, string Version);

    public async Task DeleteAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        var role = await LoadAsync(roleId, cancellationToken);
        if (role.IsSystemRole)
        {
            throw new ConflictException("Los roles del sistema no se pueden eliminar.");
        }

        _dbContext.Roles.Remove(role);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PermissionView>> GetAllPermissionsAsync(CancellationToken cancellationToken = default)
    {
        var catalog = PermissionCodes.All.ToArray();
        return await _dbContext.Permissions
            .Where(permission => catalog.Contains(permission.Code))
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
            ?? throw new NotFoundException("No encontramos el rol.");
    }

    private async Task<List<Permission>> LoadPermissionsAsync(IReadOnlyList<Guid> permissionIds, CancellationToken cancellationToken)
    {
        var uniqueIds = (permissionIds ?? []).Distinct().ToArray();
        if (uniqueIds.Length == 0)
        {
            return [];
        }

        var catalog = PermissionCodes.All.ToArray();
        var permissions = await _dbContext.Permissions
            .Where(permission => uniqueIds.Contains(permission.Id) && catalog.Contains(permission.Code))
            .ToListAsync(cancellationToken);

        if (permissions.Count != uniqueIds.Length)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["permissionIds"] = ["Alguno de los permisos elegidos no existe."]
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
            throw new ConflictException("Ya existe un rol con ese nombre.");
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length is < NameMinLength or > NameMaxLength)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["name"] = [$"El nombre debe tener entre {NameMinLength} y {NameMaxLength} caracteres."]
            });
        }
    }

    private static RoleView Map(Role role) => Map(role, null);

    private static RoleView Map(Role role, string? version)
        => new(
            role.Id,
            role.Name,
            role.IsSystemRole,
            role.RolePermissions
                .Where(rolePermission => PermissionCodes.IsInCatalog(rolePermission.Permission.Code))
                .Select(rolePermission => new PermissionView(rolePermission.Permission.Id, rolePermission.Permission.Code, rolePermission.Permission.Description))
                .OrderBy(permission => permission.Code, StringComparer.Ordinal)
                .ToArray(),
            role.CanEditPermissions,
            version);
}
