using System.Security.Claims;
using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Barbershop.Application.Customers.Claims;

namespace Api.Barbershop.Features.Customer.OwnProfile;

public static class OwnCustomerProfileEndpoints
{
    /// <summary>
    /// The signed-in customer's own customer profile: create a new one ("I am a new customer") or ask to be linked to
    /// their historical one ("I am already a customer"). Only the caller's own data is ever returned.
    /// </summary>
    public static RouteGroupBuilder MapOwnCustomerProfileEndpoints(this RouteGroupBuilder api)
    {
        var ownProfile = api.MapGroup("/customer/customer-profile")
            .WithTags("Customer")
            .RequireAuthorization(AuthPolicyNames.Customer);

        ownProfile.MapGet(string.Empty, GetStatusAsync)
            .WithName("GetOwnCustomerProfileStatus")
            .Produces<OwnCustomerStatusView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // 200 rather than 201: a retry returns the profile already created.
        ownProfile.MapPost(string.Empty, CreateOwnProfileAsync)
            .WithName("CreateOwnCustomerProfile")
            .Produces<OwnCustomerProfileView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        ownProfile.MapPost("/claims", SubmitClaimAsync)
            .WithName("SubmitOwnCustomerClaim")
            .Produces<OwnCustomerClaimView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static Task<OwnCustomerStatusView> GetStatusAsync(
        ClaimsPrincipal user, IOwnCustomerProfileService service, CancellationToken cancellationToken)
        => service.GetStatusAsync(user.GetRequiredUserId(), cancellationToken);

    private static Task<OwnCustomerProfileView> CreateOwnProfileAsync(
        OwnCustomerProfileCreateRequest request, ClaimsPrincipal user, IOwnCustomerProfileService service, CancellationToken cancellationToken)
        => service.CreateOwnProfileAsync(user.GetRequiredUserId(), request, cancellationToken);

    private static Task<OwnCustomerClaimView> SubmitClaimAsync(
        CustomerClaimCreateRequest request, ClaimsPrincipal user, IOwnCustomerProfileService service, CancellationToken cancellationToken)
        => service.SubmitClaimAsync(user.GetRequiredUserId(), request, cancellationToken);
}
