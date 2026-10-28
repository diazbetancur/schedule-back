using Barbershop.Application.Auth;
using Barbershop.Application.Memberships.Admin;

namespace Api.Barbershop.Features.Admin.Memberships;

public static class AdminMembershipPlansEndpoints
{
    public static RouteGroupBuilder MapAdminMembershipPlansEndpoints(this RouteGroupBuilder api)
    {
        var adminPlans = api.MapGroup("/admin/membership-plans")
            .WithTags("Admin")
            .RequireAuthorization(AuthPolicyNames.Admin);

        adminPlans.MapGet(string.Empty, GetAllAsync)
            .WithName("GetAdminMembershipPlans")
            .Produces<IReadOnlyList<MembershipPlanView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminPlans.MapGet("/{planId:guid}", GetByIdAsync)
            .WithName("GetAdminMembershipPlanById")
            .Produces<MembershipPlanView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminPlans.MapPost(string.Empty, CreateAsync)
            .WithName("CreateAdminMembershipPlan")
            .Produces<MembershipPlanView>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminPlans.MapPut("/{planId:guid}", UpdateAsync)
            .WithName("UpdateAdminMembershipPlan")
            .Produces<MembershipPlanView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminPlans.MapPatch("/{planId:guid}/status", UpdateStatusAsync)
            .WithName("UpdateAdminMembershipPlanStatus")
            .Produces<MembershipPlanView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminPlans.MapDelete("/{planId:guid}", SoftDeleteAsync)
            .WithName("DeleteAdminMembershipPlan")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static Task<IReadOnlyList<MembershipPlanView>> GetAllAsync(
        IAdminMembershipPlansService service, CancellationToken cancellationToken)
        => service.GetAllAsync(cancellationToken);

    private static Task<MembershipPlanView> GetByIdAsync(
        Guid planId, IAdminMembershipPlansService service, CancellationToken cancellationToken)
        => service.GetByIdAsync(planId, cancellationToken);

    private static async Task<IResult> CreateAsync(
        MembershipPlanUpsertRequest request, IAdminMembershipPlansService service, CancellationToken cancellationToken)
    {
        var response = await service.CreateAsync(request, cancellationToken);
        return Results.Created($"/api/v1/admin/membership-plans/{response.Id}", response);
    }

    private static Task<MembershipPlanView> UpdateAsync(
        Guid planId, MembershipPlanUpsertRequest request, IAdminMembershipPlansService service, CancellationToken cancellationToken)
        => service.UpdateAsync(planId, request, cancellationToken);

    private static Task<MembershipPlanView> UpdateStatusAsync(
        Guid planId, MembershipPlanStatusUpdateRequest request, IAdminMembershipPlansService service, CancellationToken cancellationToken)
        => service.UpdateStatusAsync(planId, request, cancellationToken);

    private static async Task<IResult> SoftDeleteAsync(
        Guid planId, IAdminMembershipPlansService service, CancellationToken cancellationToken)
    {
        await service.SoftDeleteAsync(planId, cancellationToken);
        return Results.NoContent();
    }
}
