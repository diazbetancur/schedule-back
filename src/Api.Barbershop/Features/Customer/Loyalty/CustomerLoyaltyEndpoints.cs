using System.Security.Claims;
using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
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

        return api;
    }

    private static Task<MyLoyaltyView> GetMineAsync(ClaimsPrincipal user, IMyLoyaltyService service, CancellationToken cancellationToken)
        => service.GetMineAsync(user.GetRequiredUserId(), cancellationToken);
}
