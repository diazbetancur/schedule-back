using System.Security.Claims;
using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Barbershop.Application.Common.Paging;
using Barbershop.Application.Loyalty.Redemptions;

namespace Api.Barbershop.Features.Admin.LoyaltyRedemptions;

public static class AdminLoyaltyRedemptionsEndpoints
{
    /// <summary>
    /// Rewards catalog (loyalty.rewards.manage, token claim) and redemptions (ADR-0015). Approving, rejecting,
    /// cancelling and registering at the counter need loyalty.redemptions.approve; delivering needs
    /// loyalty.redemptions.deliver. Both are critical: checked against the database on every request.
    /// </summary>
    public static RouteGroupBuilder MapAdminLoyaltyRedemptionsEndpoints(this RouteGroupBuilder api)
    {
        var rewards = api.MapGroup("/admin/loyalty/rewards")
            .WithTags("Admin")
            .RequireAuthorization(PermissionPolicyNames.LoyaltyRewardsManage);

        rewards.MapGet(string.Empty, ListRewardsAsync)
            .WithName("GetAdminLoyaltyRewards")
            .Produces<IReadOnlyList<LoyaltyRewardView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        rewards.MapGet("/service-options", GetServiceOptionsAsync)
            .WithName("GetAdminLoyaltyRewardServiceOptions")
            .Produces<IReadOnlyList<LoyaltyRewardServiceOption>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        rewards.MapPost(string.Empty, CreateRewardAsync)
            .WithName("CreateAdminLoyaltyReward")
            .Produces<LoyaltyRewardView>(StatusCodes.Status201Created)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        rewards.MapPut("/{rewardId:guid}", UpdateRewardAsync)
            .WithName("UpdateAdminLoyaltyReward")
            .Produces<LoyaltyRewardView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        rewards.MapGet("/{rewardId:guid}/changes", GetRewardChangesAsync)
            .WithName("GetAdminLoyaltyRewardChanges")
            .Produces<PagedResult<LoyaltyRewardChangeView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        var redemptions = api.MapGroup("/admin/loyalty/redemptions").WithTags("Admin");

        redemptions.MapGet(string.Empty, ListRedemptionsAsync)
            .RequireAuthorization(PermissionPolicyNames.LoyaltyRedemptionsApprove)
            .WithName("GetAdminLoyaltyRedemptions")
            .Produces<PagedResult<LoyaltyRedemptionView>>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        redemptions.MapGet("/rewards", ListActiveRewardsAsync)
            .RequireAuthorization(PermissionPolicyNames.LoyaltyRedemptionsApprove)
            .WithName("GetAdminLoyaltyRedemptionRewards")
            .Produces<IReadOnlyList<LoyaltyRewardView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        redemptions.MapGet("/deliveries", ListDeliveriesAsync)
            .RequireAuthorization(PermissionPolicyNames.LoyaltyRedemptionsDeliver)
            .WithName("GetAdminLoyaltyRedemptionDeliveries")
            .Produces<PagedResult<LoyaltyRedemptionView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        MapDecision(redemptions.MapPost("/{redemptionId:guid}/approve", ApproveAsync), PermissionPolicyNames.LoyaltyRedemptionsApprove, "ApproveAdminLoyaltyRedemption");
        MapDecision(redemptions.MapPost("/{redemptionId:guid}/reject", RejectAsync), PermissionPolicyNames.LoyaltyRedemptionsApprove, "RejectAdminLoyaltyRedemption");
        MapDecision(redemptions.MapPost("/{redemptionId:guid}/cancel", CancelAsync), PermissionPolicyNames.LoyaltyRedemptionsApprove, "CancelAdminLoyaltyRedemption");
        MapDecision(redemptions.MapPost("/{redemptionId:guid}/deliver", DeliverAsync), PermissionPolicyNames.LoyaltyRedemptionsDeliver, "DeliverAdminLoyaltyRedemption");

        redemptions.MapPost("/customers/{customerId:guid}", CreateAtCounterAsync)
            .RequireAuthorization(PermissionPolicyNames.LoyaltyRedemptionsApprove)
            .WithName("CreateAdminCounterLoyaltyRedemption")
            .Produces<LoyaltyRedemptionView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static void MapDecision(RouteHandlerBuilder builder, string policy, string name)
        => builder
            .RequireAuthorization(policy)
            .WithName(name)
            .Produces<LoyaltyRedemptionView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

    private static Task<IReadOnlyList<LoyaltyRewardView>> ListRewardsAsync(IAdminLoyaltyRewardsService service, CancellationToken cancellationToken)
        => service.ListAsync(cancellationToken);

    private static Task<IReadOnlyList<LoyaltyRewardServiceOption>> GetServiceOptionsAsync(
        IAdminLoyaltyRewardsService service, CancellationToken cancellationToken)
        => service.GetServiceOptionsAsync(cancellationToken);

    private static async Task<IResult> CreateRewardAsync(
        LoyaltyRewardUpsertRequest request, ClaimsPrincipal user, IAdminLoyaltyRewardsService service, CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(user.GetRequiredUserId(), request, cancellationToken);
        return Results.Created($"/api/v1/admin/loyalty/rewards/{created.Id}", created);
    }

    private static Task<LoyaltyRewardView> UpdateRewardAsync(
        Guid rewardId, LoyaltyRewardUpsertRequest request, ClaimsPrincipal user, IAdminLoyaltyRewardsService service,
        CancellationToken cancellationToken)
        => service.UpdateAsync(user.GetRequiredUserId(), rewardId, request, cancellationToken);

    private static Task<PagedResult<LoyaltyRewardChangeView>> GetRewardChangesAsync(
        Guid rewardId, [AsParameters] PageQuery query, IAdminLoyaltyRewardsService service, CancellationToken cancellationToken)
        => service.GetChangesAsync(rewardId, query, cancellationToken);

    private static Task<PagedResult<LoyaltyRedemptionView>> ListRedemptionsAsync(
        [AsParameters] LoyaltyRedemptionListQuery query, IAdminLoyaltyRedemptionsService service, CancellationToken cancellationToken)
        => service.ListAsync(query, cancellationToken);

    private static Task<IReadOnlyList<LoyaltyRewardView>> ListActiveRewardsAsync(
        IAdminLoyaltyRedemptionsService service, CancellationToken cancellationToken)
        => service.ListActiveRewardsAsync(cancellationToken);

    private static Task<PagedResult<LoyaltyRedemptionView>> ListDeliveriesAsync(
        [AsParameters] PageQuery query, IAdminLoyaltyRedemptionsService service, CancellationToken cancellationToken)
        => service.ListDeliveriesAsync(query, cancellationToken);

    private static Task<LoyaltyRedemptionView> ApproveAsync(
        Guid redemptionId, LoyaltyRedemptionDecisionRequest request, ClaimsPrincipal user, IAdminLoyaltyRedemptionsService service,
        CancellationToken cancellationToken)
        => service.ApproveAsync(user.GetRequiredUserId(), redemptionId, request, cancellationToken);

    private static Task<LoyaltyRedemptionView> RejectAsync(
        Guid redemptionId, LoyaltyRedemptionReasonRequest request, ClaimsPrincipal user, IAdminLoyaltyRedemptionsService service,
        CancellationToken cancellationToken)
        => service.RejectAsync(user.GetRequiredUserId(), redemptionId, request, cancellationToken);

    private static Task<LoyaltyRedemptionView> CancelAsync(
        Guid redemptionId, LoyaltyRedemptionReasonRequest request, ClaimsPrincipal user, IAdminLoyaltyRedemptionsService service,
        CancellationToken cancellationToken)
        => service.CancelAsync(user.GetRequiredUserId(), redemptionId, request, cancellationToken);

    private static Task<LoyaltyRedemptionView> DeliverAsync(
        Guid redemptionId, LoyaltyRedemptionDecisionRequest request, ClaimsPrincipal user, IAdminLoyaltyRedemptionsService service,
        CancellationToken cancellationToken)
        => service.DeliverAsync(user.GetRequiredUserId(), redemptionId, request, cancellationToken);

    private static Task<LoyaltyRedemptionView> CreateAtCounterAsync(
        Guid customerId, LoyaltyCounterRedemptionRequest request, ClaimsPrincipal user, IAdminLoyaltyRedemptionsService service,
        CancellationToken cancellationToken)
        => service.CreateAtCounterAsync(user.GetRequiredUserId(), customerId, request, cancellationToken);
}
