using System.Security.Claims;
using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Barbershop.Application.Memberships.Portal;

namespace Api.Barbershop.Features.Customer.Memberships;

/// <summary>Read-only: the owner is always the signed-in user's linked customer profile.</summary>
public static class CustomerMembershipsEndpoints
{
  public static RouteGroupBuilder MapCustomerMembershipsEndpoints(this RouteGroupBuilder api)
  {
    var memberships = api.MapGroup("/customer/memberships")
        .WithTags("Customer")
        .RequireAuthorization(AuthPolicyNames.Customer);

    memberships.MapGet(string.Empty, GetMineAsync)
        .WithName("GetCustomerMemberships")
        .Produces<IReadOnlyList<MyMembershipSummaryView>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    memberships.MapGet("/{membershipId:guid}", GetMineByIdAsync)
        .WithName("GetCustomerMembershipById")
        .Produces<MyMembershipView>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

    return api;
  }

  private static Task<IReadOnlyList<MyMembershipSummaryView>> GetMineAsync(
      ClaimsPrincipal user, IMyMembershipsService service, CancellationToken cancellationToken)
      => service.GetMineAsync(user.GetRequiredUserId(), cancellationToken);

  private static Task<MyMembershipView> GetMineByIdAsync(
      Guid membershipId, ClaimsPrincipal user, IMyMembershipsService service, CancellationToken cancellationToken)
      => service.GetMineByIdAsync(user.GetRequiredUserId(), membershipId, cancellationToken);
}
