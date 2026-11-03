using System.Security.Claims;
using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Barbershop.Application.Loyalty.Admin;

namespace Api.Barbershop.Features.Admin.Loyalty;

public static class AdminLoyaltyEndpoints
{
    /// <summary>
    /// Global loyalty configuration. loyalty.settings.manage is a critical permission, so every request, reads
    /// included, is checked against the database: a revocation applies to the next request with the same token.
    /// </summary>
    public static RouteGroupBuilder MapAdminLoyaltyEndpoints(this RouteGroupBuilder api)
    {
        var adminLoyalty = api.MapGroup("/admin/loyalty")
            .WithTags("Admin")
            .RequireAuthorization(PermissionPolicyNames.LoyaltySettingsManage);

        adminLoyalty.MapGet("/settings", GetSettingsAsync)
            .WithName("GetAdminLoyaltySettings")
            .Produces<LoyaltySettingsView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminLoyalty.MapPut("/settings", UpdateSettingsAsync)
            .WithName("UpdateAdminLoyaltySettings")
            .Produces<LoyaltySettingsView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminLoyalty.MapGet("/settings/changes", GetSettingsChangesAsync)
            .WithName("GetAdminLoyaltySettingsChanges")
            .Produces<IReadOnlyList<LoyaltySettingsChangeView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static Task<LoyaltySettingsView> GetSettingsAsync(
        IAdminLoyaltySettingsService service, CancellationToken cancellationToken)
        => service.GetAsync(cancellationToken);

    private static Task<LoyaltySettingsView> UpdateSettingsAsync(
        LoyaltySettingsUpdateRequest request, ClaimsPrincipal user, IAdminLoyaltySettingsService service, CancellationToken cancellationToken)
        => service.UpdateAsync(user.GetRequiredUserId(), request, cancellationToken);

    private static Task<IReadOnlyList<LoyaltySettingsChangeView>> GetSettingsChangesAsync(
        IAdminLoyaltySettingsService service, CancellationToken cancellationToken)
        => service.GetChangesAsync(cancellationToken);
}
