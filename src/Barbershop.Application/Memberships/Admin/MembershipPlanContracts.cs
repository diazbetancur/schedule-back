namespace Barbershop.Application.Memberships.Admin;

// Mode, Kind and Condition travel as strings ("Pool" | "PerService", "Service" | "Physical",
// "EveryPurchase" | "FirstPurchaseOfPlan") so invalid values come back as field validation errors.

public sealed record MembershipPlanServiceView(
    Guid ServiceId,
    string ServiceName,
    int ServiceBasePrice,
    bool ServiceIsActive,
    int? Quantity);

public sealed record MembershipPlanGiftRuleView(
    Guid Id,
    string Kind,
    Guid? ServiceId,
    string? ServiceName,
    string? Description,
    int Quantity,
    string Condition);

public sealed record MembershipPlanView(
    Guid Id,
    string Name,
    string? Description,
    int Price,
    string Mode,
    int? PoolQuantity,
    int DefaultDurationDays,
    string Terms,
    string? SpecificConditions,
    bool IsActive,
    IReadOnlyList<MembershipPlanServiceView> Services,
    IReadOnlyList<MembershipPlanGiftRuleView> GiftRules,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

/// <summary><c>Quantity</c> is required for per-service plans and must be null for pool plans.</summary>
public sealed record MembershipPlanServiceRequest(Guid ServiceId, int? Quantity);

/// <summary>
/// <c>Id</c> identifies an existing rule being kept or edited; omit it for a new rule. Rules left out of
/// an update are removed (soft-deleted).
/// </summary>
public sealed record MembershipPlanGiftRuleRequest(
    Guid? Id,
    string Kind,
    Guid? ServiceId,
    string? Description,
    int Quantity,
    string Condition);

public sealed record MembershipPlanUpsertRequest(
    string Name,
    string? Description,
    int Price,
    string Mode,
    int? PoolQuantity,
    int DefaultDurationDays,
    string Terms,
    string? SpecificConditions,
    IReadOnlyList<MembershipPlanServiceRequest>? Services,
    IReadOnlyList<MembershipPlanGiftRuleRequest>? GiftRules);

public sealed record MembershipPlanStatusUpdateRequest(bool IsActive);
