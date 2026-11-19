namespace Barbershop.Application.Authorization;

public sealed record PermissionView(Guid Id, string Code, string Description);

/// <param name="CanEditPermissions">ADR-0020 (BUG-091): custom roles and the Staff system role; Admin and Customer are locked.</param>
/// <param name="Version">SONDA BUG-161: the version of the role as read; send it back when saving so a concurrent change is a 409.</param>
public sealed record RoleView(Guid Id, string Name, bool IsSystemRole, IReadOnlyList<PermissionView> Permissions, bool CanEditPermissions = true, string? Version = null);

public sealed record RoleCreateRequest(string Name, IReadOnlyList<Guid> PermissionIds);

public sealed record RoleUpdateRequest(string Name, IReadOnlyList<Guid> PermissionIds, string? Version = null);
