namespace Barbershop.Application.Authorization;

public sealed record PermissionView(Guid Id, string Code, string Description);

/// <param name="CanEditPermissions">ADR-0020 (BUG-091): custom roles and the Staff system role; Admin and Customer are locked.</param>
public sealed record RoleView(Guid Id, string Name, bool IsSystemRole, IReadOnlyList<PermissionView> Permissions, bool CanEditPermissions = true);

public sealed record RoleCreateRequest(string Name, IReadOnlyList<Guid> PermissionIds);

public sealed record RoleUpdateRequest(string Name, IReadOnlyList<Guid> PermissionIds);
