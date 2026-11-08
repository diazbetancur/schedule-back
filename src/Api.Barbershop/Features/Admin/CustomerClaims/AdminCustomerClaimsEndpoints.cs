using System.Security.Claims;
using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Barbershop.Application.Customers.Claims;
using Barbershop.Application.Common.Paging;

namespace Api.Barbershop.Features.Admin.CustomerClaims;

public static class AdminCustomerClaimsEndpoints
{
    /// <summary>
    /// Review of profile claims. customers.claims.approve is critical: every request, reads included, is checked against
    /// the database, so a revocation applies to the next request with the same token.
    /// </summary>
    public static RouteGroupBuilder MapAdminCustomerClaimsEndpoints(this RouteGroupBuilder api)
    {
        var claims = api.MapGroup("/admin/customer-claims")
            .WithTags("Admin")
            .RequireAuthorization(PermissionPolicyNames.CustomerClaimsApprove);

        claims.MapGet(string.Empty, ListAsync)
            .WithName("GetAdminCustomerClaims")
            .Produces<PagedResult<CustomerClaimView>>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        claims.MapGet("/{claimId:guid}", GetByIdAsync)
            .WithName("GetAdminCustomerClaimById")
            .Produces<CustomerClaimView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        claims.MapGet("/{claimId:guid}/candidates", GetCandidatesAsync)
            .WithName("GetAdminCustomerClaimCandidates")
            .Produces<IReadOnlyList<CustomerClaimCandidate>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        claims.MapPost("/{claimId:guid}/approve", ApproveAsync)
            .WithName("ApproveAdminCustomerClaim")
            .Produces<CustomerClaimView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        claims.MapPost("/{claimId:guid}/reject", RejectAsync)
            .WithName("RejectAdminCustomerClaim")
            .Produces<CustomerClaimView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static Task<PagedResult<CustomerClaimView>> ListAsync(
        string? status, int? page, int? pageSize, IAdminCustomerClaimsService service, CancellationToken cancellationToken)
        => service.ListAsync(status, new PageQuery(page, pageSize), cancellationToken);

    private static Task<CustomerClaimView> GetByIdAsync(
        Guid claimId, IAdminCustomerClaimsService service, CancellationToken cancellationToken)
        => service.GetByIdAsync(claimId, cancellationToken);

    private static Task<IReadOnlyList<CustomerClaimCandidate>> GetCandidatesAsync(
        Guid claimId, string? q, IAdminCustomerClaimsService service, CancellationToken cancellationToken)
        => service.GetCandidatesAsync(claimId, q, cancellationToken);

    private static Task<CustomerClaimView> ApproveAsync(
        Guid claimId, CustomerClaimApproveRequest request, ClaimsPrincipal user, IAdminCustomerClaimsService service, CancellationToken cancellationToken)
        => service.ApproveAsync(user.GetRequiredUserId(), claimId, request, cancellationToken);

    private static Task<CustomerClaimView> RejectAsync(
        Guid claimId, CustomerClaimRejectRequest request, ClaimsPrincipal user, IAdminCustomerClaimsService service, CancellationToken cancellationToken)
        => service.RejectAsync(user.GetRequiredUserId(), claimId, request, cancellationToken);
}
