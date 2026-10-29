namespace Barbershop.Application.Finance.Admin;

// Kind is "Service" or "MembershipSale". Service income has a service and a professional; a membership
// sale has neither, is 100% business and never produces a professional share.

public sealed record IncomeEntryView(
    Guid Id,
    Guid? ServiceId,
    string? ServiceName,
    int BasePrice,
    Guid? StaffProfileId,
    string? StaffDisplayName,
    int Amount,
    bool IsPromo,
    int BusinessPercentage,
    int BusinessAmount,
    int ProfessionalAmount,
    DateOnly OccurredOn,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string Kind);

/// <summary>
/// <c>Kind</c> is optional: clients that predate membership sales omit it and get a service income.
/// <c>ServiceId</c> and <c>StaffProfileId</c> are required for service income and must be omitted for a membership sale.
/// </summary>
public sealed record IncomeEntryCreateRequest(
    Guid? ServiceId,
    Guid? StaffProfileId,
    int Amount,
    bool IsPromo,
    DateOnly OccurredOn,
    string? Kind = null);

/// <summary><c>Kind</c> may be omitted (the entry keeps its kind) but, if sent, must match: the kind never changes.</summary>
public sealed record IncomeEntryUpdateRequest(
    Guid? ServiceId,
    Guid? StaffProfileId,
    int Amount,
    bool IsPromo,
    DateOnly OccurredOn,
    string? Kind = null);
