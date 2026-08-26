namespace Barbershop.Application.Authorization;

public sealed record PermissionView(Guid Id, string Code, string Description);

public sealed record RoleView(Guid Id, string Name, bool IsSystemRole, IReadOnlyList<PermissionView> Permissions);

public sealed record RoleCreateRequest(string Name, IReadOnlyList<Guid> PermissionIds);

public sealed record RoleUpdateRequest(string Name, IReadOnlyList<Guid> PermissionIds);
