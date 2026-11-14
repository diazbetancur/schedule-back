using Barbershop.Application.Common.Paging;

namespace Barbershop.Application.Loyalty.Redemptions;

// Kind travels as "Service" | "Other"; Status as "Requested" | "Approved" | "Delivered" | "Rejected" | "Cancelled" |
// "Expired"; Channel as "App" | "Counter" (ADR-0015).

// ── Catalog (loyalty.rewards.manage) ────────────────────────────────────────────────────────────

public sealed record LoyaltyRewardView(
    Guid Id,
    string Name,
    string? Description,
    string Kind,
    Guid? ServiceId,
    string? ServiceName,
    int PointsCost,
    bool IsActive,
    uint Version,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string? UpdatedByName);

/// <summary>
/// Creates or edits a reward. <c>ServiceId</c> is required for "Service" and must be omitted for "Other". Editing
/// needs the <c>Version</c> read; requests already made keep their own copy and are never changed.
/// </summary>
public sealed record LoyaltyRewardUpsertRequest(
    string? Name,
    string? Description,
    string? Kind,
    Guid? ServiceId,
    int PointsCost,
    // Omitted on create: active; omitted on update: unchanged (an omitted flag no longer re-activates a reward).
    bool? IsActive = null,
    uint? Version = null);

public sealed record LoyaltyRewardChangeView(
    Guid Id,
    string ChangeType,
    DateTime ChangedAt,
    string? ChangedByName,
    string? PreviousName,
    string NewName,
    string? PreviousDescription,
    string? NewDescription,
    string? PreviousKind,
    string NewKind,
    string? PreviousServiceName,
    string? NewServiceName,
    int? PreviousPointsCost,
    int NewPointsCost,
    bool? PreviousIsActive,
    bool NewIsActive);

/// <summary>A settlement rate a service reward can deliver, labelled "Servicio general · Tarifa" (ADR-0016 T10).</summary>
public sealed record LoyaltyRewardServiceOption(Guid Id, string Name, bool IsActive, Guid GeneralServiceId, string GeneralServiceName);

public interface IAdminLoyaltyRewardsService
{
    Task<IReadOnlyList<LoyaltyRewardView>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LoyaltyRewardServiceOption>> GetServiceOptionsAsync(CancellationToken cancellationToken = default);

    Task<LoyaltyRewardView> CreateAsync(Guid currentUserId, LoyaltyRewardUpsertRequest request, CancellationToken cancellationToken = default);

    Task<LoyaltyRewardView> UpdateAsync(
        Guid currentUserId, Guid rewardId, LoyaltyRewardUpsertRequest request, CancellationToken cancellationToken = default);

    /// <summary>Change log of one reward, newest first, paginated.</summary>
    Task<PagedResult<LoyaltyRewardChangeView>> GetChangesAsync(
        Guid rewardId, PageQuery? query = null, CancellationToken cancellationToken = default);
}

// ── Redemptions ─────────────────────────────────────────────────────────────────────────────────

/// <summary>Points a redemption reserved from one lot (released or consumed exactly once).</summary>
public sealed record LoyaltyRedemptionAllocationView(Guid LotId, int Points, DateOnly ExpiresOn);

/// <summary>
/// A redemption as staff see it, with the reward as it was when requested. <c>IsPastDeadline</c>: still pending but
/// past its deadline (approval or delivery); it can no longer be approved or delivered, and the expiration job will
/// record it and release its points.
/// </summary>
public sealed record LoyaltyRedemptionView(
    Guid Id,
    Guid CustomerId,
    string? CustomerName,
    Guid RewardId,
    string RewardName,
    string RewardKind,
    Guid? ServiceId,
    string? ServiceName,
    int PointsCost,
    string Status,
    string Channel,
    bool InPersonVerified,
    DateTime RequestedAt,
    string? RequestedByName,
    DateOnly RequestedOn,
    int ApprovalDays,
    int DeliveryDays,
    DateOnly ApproveBy,
    DateTime? ApprovedAt,
    string? ApprovedByName,
    DateOnly? DeliverBy,
    DateTime? DeliveredAt,
    string? DeliveredByName,
    Guid? ServiceRecordId,
    DateTime? RejectedAt,
    string? RejectedByName,
    string? RejectionReason,
    DateTime? CancelledAt,
    string? CancelledByName,
    string? CancellationReason,
    bool CancelledByCustomer,
    DateTime? ExpiredAt,
    bool IsPastDeadline,
    IReadOnlyList<LoyaltyRedemptionAllocationView> Allocations,
    uint Version);

/// <summary><c>Version</c> is the one read; a stale one is a conflict.</summary>
public sealed record LoyaltyRedemptionDecisionRequest(uint? Version);

/// <summary>Rejection or cancellation by staff: the reason is required.</summary>
public sealed record LoyaltyRedemptionReasonRequest(uint? Version, string? Reason);

/// <summary>
/// A redemption registered at the counter for a customer who is present (R3): the in-person verification is
/// mandatory, and <c>ApproveNow</c> approves it in the same step (both actions are recorded).
/// </summary>
public sealed record LoyaltyCounterRedemptionRequest(Guid RewardId, bool InPersonVerified, bool ApproveNow, Guid IdempotencyKey);

public interface IAdminLoyaltyRedemptionsService
{
    /// <summary>
    /// Paginated (ADR-0016 B7). Filtering by a pending status lists oldest first (a queue); any other view lists newest
    /// first. Ties are broken by id.
    /// </summary>
    Task<PagedResult<LoyaltyRedemptionView>> ListAsync(LoyaltyRedemptionListQuery query, CancellationToken cancellationToken = default);

    /// <summary>Active rewards, for registering a redemption at the counter (no catalog permission needed).</summary>
    Task<IReadOnlyList<LoyaltyRewardView>> ListActiveRewardsAsync(CancellationToken cancellationToken = default);

    /// <summary>Approved redemptions waiting for delivery (a queue: no date filter), oldest deadline first, paginated.</summary>
    Task<PagedResult<LoyaltyRedemptionView>> ListDeliveriesAsync(PageQuery? query = null, CancellationToken cancellationToken = default);

    Task<LoyaltyRedemptionView> GetAsync(Guid redemptionId, CancellationToken cancellationToken = default);

    Task<LoyaltyRedemptionView> ApproveAsync(
        Guid currentUserId, Guid redemptionId, LoyaltyRedemptionDecisionRequest request, CancellationToken cancellationToken = default);

    Task<LoyaltyRedemptionView> RejectAsync(
        Guid currentUserId, Guid redemptionId, LoyaltyRedemptionReasonRequest request, CancellationToken cancellationToken = default);

    Task<LoyaltyRedemptionView> CancelAsync(
        Guid currentUserId, Guid redemptionId, LoyaltyRedemptionReasonRequest request, CancellationToken cancellationToken = default);

    /// <summary>Delivers an "Other" reward. A service reward is delivered only by confirming its service record.</summary>
    Task<LoyaltyRedemptionView> DeliverAsync(
        Guid currentUserId, Guid redemptionId, LoyaltyRedemptionDecisionRequest request, CancellationToken cancellationToken = default);

    Task<LoyaltyRedemptionView> CreateAtCounterAsync(
        Guid currentUserId, Guid customerId, LoyaltyCounterRedemptionRequest request, CancellationToken cancellationToken = default);
}

// ── Customer ("Mis puntos") ─────────────────────────────────────────────────────────────────────

public sealed record MyLoyaltyRewardView(
    Guid Id, string Name, string? Description, string Kind, string? ServiceName, int PointsCost, bool CanAfford);

/// <summary>Active rewards with whether the available points (usable minus debt) cover them.</summary>
public sealed record MyLoyaltyCatalogView(bool HasProfile, bool ProgramEnabled, int AvailablePoints, IReadOnlyList<MyLoyaltyRewardView> Rewards);

public sealed record MyLoyaltyRedemptionView(
    Guid Id,
    string RewardName,
    string RewardKind,
    string? ServiceName,
    int PointsCost,
    string Status,
    DateTime RequestedAt,
    DateOnly ApproveBy,
    DateTime? ApprovedAt,
    DateOnly? DeliverBy,
    DateTime? DeliveredAt,
    DateTime? RejectedAt,
    string? RejectionReason,
    DateTime? CancelledAt,
    bool CancelledByCustomer,
    DateTime? ExpiredAt,
    bool IsPastDeadline,
    bool CanCancel);

/// <summary><c>IdempotencyKey</c> stays the same for retries of one intended request.</summary>
public sealed record LoyaltyRedemptionRequest(Guid RewardId, Guid IdempotencyKey);

public sealed record MyLoyaltyRedemptionCancelRequest(string? Reason);

public interface IMyLoyaltyRedemptionsService
{
    Task<MyLoyaltyCatalogView> GetCatalogAsync(Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Own redemptions, paginated: the pending ones (requested or approved) first, then the rest, newest request first.
    /// </summary>
    Task<PagedResult<MyLoyaltyRedemptionView>> ListMineAsync(
        Guid currentUserId, PageQuery? query = null, CancellationToken cancellationToken = default);

    Task<MyLoyaltyRedemptionView> RequestAsync(Guid currentUserId, LoyaltyRedemptionRequest request, CancellationToken cancellationToken = default);

    Task<MyLoyaltyRedemptionView> CancelMineAsync(
        Guid currentUserId, Guid redemptionId, MyLoyaltyRedemptionCancelRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Filters of <c>GET /admin/loyalty/redemptions</c> (ADR-0016 B7): <c>Status</c> name, <c>CustomerId</c> and an
/// inclusive range of Bogotá dates of the request.
/// </summary>
public sealed record LoyaltyRedemptionListQuery(
    string? Status = null,
    Guid? CustomerId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    int? Page = null,
    int? PageSize = null);
