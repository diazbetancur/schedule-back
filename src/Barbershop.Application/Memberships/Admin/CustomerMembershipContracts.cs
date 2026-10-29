namespace Barbershop.Application.Memberships.Admin;

// Status is the stored state: "PendingActivation" | "Active" | "Annulled". DisplayStatus also derives the
// dates: "PendingActivation" | "Scheduled" (active, not started yet) | "Active" | "Expired" | "Annulled".

/// <summary>
/// <c>SoldOn</c> defaults to today (Bogotá) and <c>EndsOn</c> to StartsOn + the plan's default duration − 1
/// (inclusive). Activating now a membership whose end date already passed needs
/// <c>ConfirmExpiredActivation</c> and an <c>ActivationReason</c>.
/// </summary>
public sealed record MembershipSaleRequest(
    Guid PlanId,
    DateOnly StartsOn,
    DateOnly? EndsOn = null,
    DateOnly? SoldOn = null,
    bool ActivateNow = false,
    bool ConfirmExpiredActivation = false,
    string? ActivationReason = null);

/// <summary>Activating after the end date is exceptional: it needs the confirmation and a reason.</summary>
public sealed record MembershipActivateRequest(bool ConfirmExpiredActivation = false, string? Reason = null);

public sealed record MembershipAnnulRequest(string Reason);

/// <summary>
/// Registers that <c>Quantity</c> units of <c>ServiceId</c> were given on <c>ServiceDate</c> (Bogotá date, within the
/// validity and not in the future). Gifted units are used before purchased ones. <c>IdempotencyKey</c> is generated
/// once per intended consumption: resending the same request returns the already registered consumption.
/// </summary>
public sealed record MembershipConsumptionRequest(
    Guid ServiceId,
    int Quantity,
    DateOnly ServiceDate,
    Guid? StaffProfileId,
    Guid IdempotencyKey);

/// <summary>Reverses a whole consumption; the reason is required.</summary>
public sealed record MembershipConsumptionReverseRequest(string Reason);

/// <summary>
/// Links the membership in the route, plus <c>AdditionalMembershipIds</c> paid with the same income, in one
/// step (so a payment for several memberships is checked against their total, not one at a time).
/// <c>DiscrepancyJustification</c> is required when the income amount differs from the sum of the prices of
/// every membership it would then cover.
/// </summary>
public sealed record MembershipIncomeLinkRequest(
    Guid IncomeEntryId,
    IReadOnlyList<Guid>? AdditionalMembershipIds = null,
    string? DiscrepancyJustification = null);

/// <summary>Required justification when the memberships left on the income no longer add up to its amount.</summary>
public sealed record MembershipIncomeUnlinkRequest(string? DiscrepancyJustification = null);

public sealed record CustomerMembershipSummaryView(
    Guid Id,
    Guid CustomerId,
    string PlanName,
    int Price,
    string Mode,
    string Status,
    string DisplayStatus,
    DateOnly SoldOn,
    DateOnly StartsOn,
    DateOnly EndsOn,
    int PurchasedQuantity,
    int GiftedServiceQuantity,
    int PhysicalGiftCount,
    Guid? IncomeEntryId,
    DateTime CreatedAt);

public sealed record MembershipAllowanceServiceView(Guid ServiceId, string ServiceName);

/// <summary>
/// A balance line. <c>ConsumedQuantity</c> counts consumptions minus their reversals and
/// <c>AvailableQuantity</c> = Granted − Consumed. On an annulled membership the available units stay blocked.
/// </summary>
public sealed record MembershipAllowanceView(
    Guid Id,
    string Origin,
    Guid? GiftId,
    int GrantedQuantity,
    int ConsumedQuantity,
    int AvailableQuantity,
    IReadOnlyList<MembershipAllowanceServiceView> Services);

/// <summary>
/// Status: service gifts are "Granted" or "Blocked" (annulled membership); physical gifts are
/// "PendingDelivery" or "NotDeliverable" (annulled membership). Delivery is recorded in a later stage.
/// </summary>
public sealed record MembershipGiftView(
    Guid Id,
    string Kind,
    string Condition,
    int Quantity,
    Guid? ServiceId,
    string? ServiceName,
    string? Description,
    string Status);

public sealed record MembershipLinkedPeerView(Guid MembershipId, string CustomerName, string PlanName, int Price, string Status);

public sealed record MembershipIncomeView(
    Guid IncomeEntryId,
    int Amount,
    DateOnly OccurredOn,
    bool IsDeleted,
    int LinkedMembershipsTotal,
    bool HasDiscrepancy,
    IReadOnlyList<MembershipLinkedPeerView> LinkedMemberships);

public sealed record MembershipEventView(
    Guid Id,
    string Type,
    DateTime PerformedAt,
    string PerformedByName,
    string? Reason,
    Guid? IncomeEntryId,
    Guid? RelatedCustomerId,
    string? RelatedCustomerName);

/// <summary>Units a consumption took from one balance line ("Gift" or "Purchased").</summary>
public sealed record MembershipConsumptionPartView(Guid AllowanceId, string Origin, int Quantity);

public sealed record MembershipConsumptionReversalView(string Reason, DateTime PerformedAt, string PerformedByName);

public sealed record MembershipConsumptionView(
    Guid Id,
    Guid ServiceId,
    string ServiceName,
    int Quantity,
    DateOnly ServiceDate,
    Guid? StaffProfileId,
    string? StaffName,
    DateTime RegisteredAt,
    string RegisteredByName,
    IReadOnlyList<MembershipConsumptionPartView> Parts,
    MembershipConsumptionReversalView? Reversal);

public sealed record CustomerMembershipDetailView(
    Guid Id,
    Guid CustomerId,
    string CustomerName,
    Guid OriginalCustomerId,
    string OriginalCustomerName,
    Guid MembershipPlanId,
    string PlanName,
    string? PlanDescription,
    int Price,
    string Mode,
    string Terms,
    string? SpecificConditions,
    DateOnly SoldOn,
    DateOnly StartsOn,
    DateOnly EndsOn,
    string Status,
    string DisplayStatus,
    DateTime? ActivatedAt,
    string? ActivatedByName,
    DateTime? AnnulledAt,
    string? AnnulledByName,
    string? AnnulmentReason,
    IReadOnlyList<MembershipAllowanceView> Allowances,
    IReadOnlyList<MembershipGiftView> Gifts,
    MembershipIncomeView? Income,
    IReadOnlyList<MembershipEventView> Events,
    IReadOnlyList<MembershipConsumptionView> Consumptions,
    DateTime CreatedAt);

/// <summary>A membership-sale income an admin can link, with the memberships it already covers.</summary>
public sealed record MembershipIncomeCandidateView(
    Guid IncomeEntryId,
    int Amount,
    DateOnly OccurredOn,
    int LinkedMembershipsTotal,
    IReadOnlyList<MembershipLinkedPeerView> LinkedMemberships);
