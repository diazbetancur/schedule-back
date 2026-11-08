using System.Security.Claims;
using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Barbershop.Application.Common.Paging;
using Barbershop.Application.Loyalty.Wallet;

namespace Api.Barbershop.Features.Admin.CustomerLoyalty;

public static class AdminCustomerLoyaltyEndpoints
{
    /// <summary>
    /// A customer's points wallet under the same admin policy as the customers module. Manual adjustments also need
    /// the critical loyalty.points.adjust permission, checked against the database on every request.
    /// </summary>
    public static RouteGroupBuilder MapAdminCustomerLoyaltyEndpoints(this RouteGroupBuilder api)
    {
        var loyalty = api.MapGroup("/admin/customers/{customerId:guid}/loyalty")
            .WithTags("Admin")
            .RequireAuthorization(AuthPolicyNames.Admin);

        loyalty.MapGet(string.Empty, GetWalletAsync)
            .WithName("GetAdminCustomerLoyalty")
            .Produces<LoyaltyWalletView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        loyalty.MapGet("/movements", GetMovementsAsync)
            .WithName("GetAdminCustomerLoyaltyMovements")
            .Produces<PagedResult<LoyaltyMovementView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        loyalty.MapPost("/adjustments", AdjustAsync)
            .RequireAuthorization(PermissionPolicyNames.LoyaltyPointsAdjust)
            .WithName("AdjustAdminCustomerLoyalty")
            .Produces<LoyaltyWalletView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static Task<LoyaltyWalletView> GetWalletAsync(
        Guid customerId, IAdminCustomerLoyaltyService service, CancellationToken cancellationToken)
        => service.GetWalletAsync(customerId, cancellationToken);

    private static Task<PagedResult<LoyaltyMovementView>> GetMovementsAsync(
        Guid customerId, [AsParameters] LoyaltyMovementListQuery query, IAdminCustomerLoyaltyService service, CancellationToken cancellationToken)
        => service.GetMovementsAsync(customerId, query, cancellationToken);

    private static Task<LoyaltyWalletView> AdjustAsync(
        Guid customerId, LoyaltyAdjustmentRequest request, ClaimsPrincipal user, IAdminCustomerLoyaltyService service, CancellationToken cancellationToken)
        => service.AdjustAsync(user.GetRequiredUserId(), customerId, request, cancellationToken);
}
