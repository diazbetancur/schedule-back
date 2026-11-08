using Barbershop.Application.Auth;
using Barbershop.Domain.Users;
using Microsoft.AspNetCore.Authorization;

namespace Api.Barbershop.Configuration;

public static class PermissionPolicies
{
    /// <summary>Registers a policy for every code in the catalog. Admin passes them because the seed grants its role every permission, which travel as claims in its token.</summary>
    public static AuthorizationOptions AddPermissionPolicies(this AuthorizationOptions options)
    {
        foreach (var code in PermissionCodes.All)
        {
            options.AddPolicy(PermissionPolicyNames.For(code), policy =>
            {
                policy.RequireClaim(PermissionClaimTypes.Permission, code);

                // Critical permissions are also checked against the database on every request (needs
                // CurrentPermissionAuthorizationHandler registered), so a revocation is not delayed until the token expires.
                if (PermissionCodes.Critical.Contains(code))
                {
                    policy.AddRequirements(new CurrentPermissionRequirement(code));
                }
            });
        }

        return options;
    }
}
