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
