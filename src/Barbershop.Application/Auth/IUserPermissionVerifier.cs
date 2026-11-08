namespace Barbershop.Application.Auth;

/// <summary>
/// Reads from the database whether a user holds a permission right now, through the roles currently assigned to them.
/// Unlike the token's permission claims, a revoked permission, a removed role or a deactivated account is seen by the
/// very next check. Critical operations go through it via their permission policy (see
/// <see cref="PermissionPolicyNames"/>); a service can also call it directly before acting.
/// </summary>
public interface IUserPermissionVerifier
{
    /// <summary>False when the user does not exist, is inactive, or none of their roles grants the permission.</summary>
    Task<bool> HasPermissionAsync(Guid userId, string permissionCode, CancellationToken cancellationToken = default);
}
