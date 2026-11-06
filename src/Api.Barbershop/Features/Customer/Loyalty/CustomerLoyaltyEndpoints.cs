using System.Security.Claims;
using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Barbershop.Application.Loyalty.Redemptions;
using Barbershop.Application.Loyalty.Wallet;

namespace Api.Barbershop.Features.Customer.Loyalty;

public static class CustomerLoyaltyEndpoints
{
    /// <summary>The signed-in customer's own points ("Mis puntos"): only the live profile linked to the caller.</summary>
    public static RouteGroupBuilder MapCustomerLoyaltyEndpoints(this RouteGroupBuilder api)
    {
        var loyalty = api.MapGroup("/customer/loyalty")
            .WithTags("Customer")
            .RequireAuthorization(AuthPolicyNames.Customer);

        loyalty.MapGet(string.Empty, GetMineAsync)
            .WithName("GetMyLoyalty")
            .Produces<MyLoyaltyView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // Rewards and own redemptions (ADR-0015): only the live profile linked to the caller, never another one.
        loyalty.MapGet("/rewards", GetCatalogAsync)
            .WithName("GetMyLoyaltyRewards")
            .Produces<MyLoyaltyCatalogView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        loyalty.MapGet("/redemptions", ListRedemptionsAsync)
            .WithName("GetMyLoyaltyRedemptions")
            .Produces<IReadOnlyList<MyLoyaltyRedemptionView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        loyalty.MapPost("/redemptions", RequestAsync)
            .WithName("RequestMyLoyaltyRedemption")
            .Produces<MyLoyaltyRedemptionView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        loyalty.MapPost("/redemptions/{redemptionId:guid}/cancel", CancelAsync)
            .WithName("CancelMyLoyaltyRedemption")
            .Produces<MyLoyaltyRedemptionView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static Task<MyLoyaltyCatalogView> GetCatalogAsync(
        ClaimsPrincipal user, IMyLoyaltyRedemptionsService service, CancellationToken cancellationToken)
        => service.GetCatalogAsync(user.GetRequiredUserId(), cancellationToken);

    private static Task<IReadOnlyList<MyLoyaltyRedemptionView>> ListRedemptionsAsync(
        ClaimsPrincipal user, IMyLoyaltyRedemptionsService service, CancellationToken cancellationToken)
        => service.ListMineAsync(user.GetRequiredUserId(), cancellationToken);

    private static Task<MyLoyaltyRedemptionView> RequestAsync(
        LoyaltyRedemptionRequest request, ClaimsPrincipal user, IMyLoyaltyRedemptionsService service, CancellationToken cancellationToken)
        => service.RequestAsync(user.GetRequiredUserId(), request, cancellationToken);

    private static Task<MyLoyaltyRedemptionView> CancelAsync(
        Guid redemptionId, MyLoyaltyRedemptionCancelRequest request, ClaimsPrincipal user, IMyLoyaltyRedemptionsService service,
        CancellationToken cancellationToken)
        => service.CancelMineAsync(user.GetRequiredUserId(), redemptionId, request, cancellationToken);

    private static Task<MyLoyaltyView> GetMineAsync(ClaimsPrincipal user, IMyLoyaltyService service, CancellationToken cancellationToken)
        => service.GetMineAsync(user.GetRequiredUserId(), cancellationToken);
}
