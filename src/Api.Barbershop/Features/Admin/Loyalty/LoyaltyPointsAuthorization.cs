using System.Security.Claims;
using Barbershop.Application.Auth;
using Microsoft.AspNetCore.Authorization;

namespace Api.Barbershop.Features.Admin.Loyalty;

/// <summary>
/// The services and membership plans forms may carry fixed loyalty points. Sending them needs the same critical
/// policy as the loyalty settings (claim plus a database check), on top of the catalog's own admin policy.
/// </summary>
internal static class LoyaltyPointsAuthorization
{
    public static async Task EnsureCanSetAsync(int? loyaltyPoints, ClaimsPrincipal user, IAuthorizationService authorizationService)
    {
        if (loyaltyPoints is null)
        {
            return;
        }

        var result = await authorizationService.AuthorizeAsync(user, PermissionPolicyNames.LoyaltySettingsManage);
        if (!result.Succeeded)
        {
            throw new UnauthorizedAccessException("No tienes permiso para configurar los puntos de fidelización.");
        }
    }
}
