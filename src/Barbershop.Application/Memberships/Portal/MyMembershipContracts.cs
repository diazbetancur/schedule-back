namespace Barbershop.Application.Memberships.Portal;

// Read-only views for the customer portal. They are projections of the admin views (same balances, statuses
// and gift states), without admin audit data: no income, no admin names, no reasons, no other customers.
// DisplayStatus: "PendingActivation" | "Scheduled" | "Active" | "Expired" | "Annulled".

public sealed record MyMembershipSummaryView(
    Guid Id,
    string PlanName,
    string DisplayStatus,
    DateOnly StartsOn,
    DateOnly EndsOn,
    int PurchasedGranted,
    int PurchasedAvailable,
    int GiftedGranted,
    int GiftedAvailable,
    int PendingPhysicalGifts);

/// <summary>A balance line: "Purchased" or "Gift" units usable for one or more services.</summary>
public sealed record MyMembershipBalanceView(
    string Origin,
    IReadOnlyList<string> Services,
    int GrantedQuantity,
    int ConsumedQuantity,
    int AvailableQuantity);

/// <summary>Kind "Service" or "Physical"; Status "Granted" | "Blocked" | "PendingDelivery" | "Delivered" | "NotDeliverable".</summary>
public sealed record MyMembershipGiftView(
    string Kind,
    string Description,
    int Quantity,
    string Status,
    DateOnly? DeliveredOn);

public sealed record MyMembershipConsumptionPartView(string Origin, int Quantity);

/// <summary>A consumption on its real service date; a reversed one stays listed, marked as reversed.</summary>
public sealed record MyMembershipConsumptionView(
    string ServiceName,
    int Quantity,
    DateOnly ServiceDate,
    IReadOnlyList<MyMembershipConsumptionPartView> Parts,
    bool IsReversed,
    DateTime? ReversedAt);

/// <summary><c>PurchasedByName</c> is set only when the membership came from a merged duplicate profile.</summary>
public sealed record MyMembershipView(
    Guid Id,
    string PlanName,
    string? PlanDescription,
    int Price,
    string Mode,
    string DisplayStatus,
    DateOnly SoldOn,
    DateOnly StartsOn,
    DateOnly EndsOn,
    string? PurchasedByName,
    IReadOnlyList<MyMembershipBalanceView> Balances,
    IReadOnlyList<MyMembershipGiftView> Gifts,
    IReadOnlyList<MyMembershipConsumptionView> Consumptions,
    string Terms,
    string? SpecificConditions);
