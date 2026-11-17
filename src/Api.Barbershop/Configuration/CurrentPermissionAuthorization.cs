using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Microsoft.AspNetCore.Authorization;

namespace Api.Barbershop.Configuration;

/// <summary>Requires that the user still holds the permission according to the database, not just the token.</summary>
public sealed class CurrentPermissionRequirement : IAuthorizationRequirement
{
    public CurrentPermissionRequirement(string permissionCode)
    {
        PermissionCode = permissionCode;
    }

    public string PermissionCode { get; }
}

public sealed class CurrentPermissionAuthorizationHandler : AuthorizationHandler<CurrentPermissionRequirement>
{
    private readonly IUserPermissionVerifier _permissionVerifier;

    public CurrentPermissionAuthorizationHandler(IUserPermissionVerifier permissionVerifier)
    {
        _permissionVerifier = permissionVerifier;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, CurrentPermissionRequirement requirement)
    {
        if (!context.User.TryGetUserId(out var userId))
        {
            return;
        }

        var cancellationToken = context.Resource is HttpContext httpContext ? httpContext.RequestAborted : CancellationToken.None;
        if (await _permissionVerifier.HasPermissionAsync(userId, requirement.PermissionCode, cancellationToken))
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>
/// ADR-0018 D4: the role, or any of the permissions held in the token and still granted according to the database
/// (ADR-0005), so a revoked permission stops working at once.
/// </summary>
public sealed class AdminOrCurrentPermissionRequirement : IAuthorizationRequirement
{
    public AdminOrCurrentPermissionRequirement(string role, params string[] permissionCodes)
    {
        Role = role;
        PermissionCodes = permissionCodes;
    }

    public string Role { get; }

    public IReadOnlyList<string> PermissionCodes { get; }
}

public sealed class AdminOrCurrentPermissionAuthorizationHandler : AuthorizationHandler<AdminOrCurrentPermissionRequirement>
{
    private readonly IUserPermissionVerifier _permissionVerifier;

    public AdminOrCurrentPermissionAuthorizationHandler(IUserPermissionVerifier permissionVerifier)
    {
        _permissionVerifier = permissionVerifier;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminOrCurrentPermissionRequirement requirement)
    {
        if (context.User.IsInRole(requirement.Role))
        {
            context.Succeed(requirement);
            return;
        }

        if (!context.User.TryGetUserId(out var userId))
        {
            return;
        }

        var cancellationToken = context.Resource is HttpContext httpContext ? httpContext.RequestAborted : CancellationToken.None;
        foreach (var code in requirement.PermissionCodes)
        {
            if (context.User.HasClaim(PermissionClaimTypes.Permission, code)
                && await _permissionVerifier.HasPermissionAsync(userId, code, cancellationToken))
            {
                context.Succeed(requirement);
                return;
            }
        }
    }
}
