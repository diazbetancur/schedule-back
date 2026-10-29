using System.Security.Claims;
using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Barbershop.Application.Memberships.Admin;

namespace Api.Barbershop.Features.Admin.Memberships;

public static class AdminMembershipsEndpoints
{
    public static RouteGroupBuilder MapAdminMembershipsEndpoints(this RouteGroupBuilder api)
    {
        var customerMemberships = api.MapGroup("/admin/customers/{customerId:guid}/memberships")
            .WithTags("Admin")
            .RequireAuthorization(AuthPolicyNames.Admin);

        customerMemberships.MapGet(string.Empty, GetCustomerMembershipsAsync)
            .WithName("GetAdminCustomerMemberships")
            .Produces<IReadOnlyList<CustomerMembershipSummaryView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        customerMemberships.MapPost(string.Empty, SellAsync)
            .WithName("SellAdminCustomerMembership")
            .Produces<CustomerMembershipDetailView>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        var memberships = api.MapGroup("/admin/memberships")
            .WithTags("Admin")
            .RequireAuthorization(AuthPolicyNames.Admin);

        memberships.MapGet("/income-candidates", GetIncomeCandidatesAsync)
            .WithName("GetAdminMembershipIncomeCandidates")
            .Produces<IReadOnlyList<MembershipIncomeCandidateView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        memberships.MapGet("/{membershipId:guid}", GetByIdAsync)
            .WithName("GetAdminMembershipById")
            .Produces<CustomerMembershipDetailView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        memberships.MapPost("/{membershipId:guid}/activate", ActivateAsync)
            .WithName("ActivateAdminMembership")
            .Produces<CustomerMembershipDetailView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        memberships.MapPost("/{membershipId:guid}/annul", AnnulAsync)
            .WithName("AnnulAdminMembership")
            .Produces<CustomerMembershipDetailView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        memberships.MapPut("/{membershipId:guid}/income-link", LinkIncomeAsync)
            .WithName("LinkAdminMembershipIncome")
            .Produces<CustomerMembershipDetailView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        memberships.MapPost("/{membershipId:guid}/income-unlink", UnlinkIncomeAsync)
            .WithName("UnlinkAdminMembershipIncome")
            .Produces<CustomerMembershipDetailView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static Task<IReadOnlyList<CustomerMembershipSummaryView>> GetCustomerMembershipsAsync(
        Guid customerId, IAdminMembershipsService service, CancellationToken cancellationToken)
        => service.GetCustomerMembershipsAsync(customerId, cancellationToken);

    private static async Task<IResult> SellAsync(
        Guid customerId, MembershipSaleRequest request, ClaimsPrincipal user, IAdminMembershipsService service, CancellationToken cancellationToken)
    {
        var response = await service.SellAsync(user.GetRequiredUserId(), customerId, request, cancellationToken);
        return Results.Created($"/api/v1/admin/memberships/{response.Id}", response);
    }

    private static Task<IReadOnlyList<MembershipIncomeCandidateView>> GetIncomeCandidatesAsync(
        DateOnly date, IAdminMembershipsService service, CancellationToken cancellationToken)
        => service.GetIncomeCandidatesAsync(date, cancellationToken);

    private static Task<CustomerMembershipDetailView> GetByIdAsync(
        Guid membershipId, IAdminMembershipsService service, CancellationToken cancellationToken)
        => service.GetByIdAsync(membershipId, cancellationToken);

    private static Task<CustomerMembershipDetailView> ActivateAsync(
        Guid membershipId, MembershipActivateRequest request, ClaimsPrincipal user, IAdminMembershipsService service, CancellationToken cancellationToken)
        => service.ActivateAsync(user.GetRequiredUserId(), membershipId, request, cancellationToken);

    private static Task<CustomerMembershipDetailView> AnnulAsync(
        Guid membershipId, MembershipAnnulRequest request, ClaimsPrincipal user, IAdminMembershipsService service, CancellationToken cancellationToken)
        => service.AnnulAsync(user.GetRequiredUserId(), membershipId, request, cancellationToken);

    private static Task<CustomerMembershipDetailView> LinkIncomeAsync(
        Guid membershipId, MembershipIncomeLinkRequest request, ClaimsPrincipal user, IAdminMembershipsService service, CancellationToken cancellationToken)
        => service.LinkIncomeAsync(user.GetRequiredUserId(), membershipId, request, cancellationToken);

    private static Task<CustomerMembershipDetailView> UnlinkIncomeAsync(
        Guid membershipId, MembershipIncomeUnlinkRequest request, ClaimsPrincipal user, IAdminMembershipsService service, CancellationToken cancellationToken)
        => service.UnlinkIncomeAsync(user.GetRequiredUserId(), membershipId, request, cancellationToken);
}
