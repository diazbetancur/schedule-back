namespace Barbershop.Application.Finance.Admin;

// Kind is "Service" or "MembershipSale". Service income has a service and a professional; a membership
// sale has neither, is 100% business and never produces a professional share. ServiceRecordId is set when the
// income was created by confirming a service record: it is then managed from there and cannot be edited here.

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
    string Kind,
    Guid? ServiceRecordId = null);

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

/// <summary>
/// Filters of <c>GET /admin/income</c> (ADR-0016 B7). <c>From</c>/<c>To</c> are inclusive Bogotá dates; when both are
/// omitted the list shows today (the old single-day view), and a missing end takes the other end. <c>Kind</c> is
/// "Service" or "MembershipSale". <c>GeneralServiceId</c> matches the general service the rate belongs to now.
/// </summary>
public sealed record IncomeListQuery(
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? StaffProfileId = null,
    string? Kind = null,
    Guid? GeneralServiceId = null,
    int? Page = null,
    int? PageSize = null);

/// <summary>
/// One page of income plus the applied range and the total amount of every matching entry (not just this page), so
/// the page total never depends on pagination.
/// </summary>
public sealed record IncomeEntriesPage(
    IReadOnlyList<IncomeEntryView> Items,
    int Page,
    int PageSize,
    int TotalCount,
    DateOnly From,
    DateOnly To,
    long TotalAmount)
    : Barbershop.Application.Common.Paging.PagedResult<IncomeEntryView>(Items, Page, PageSize, TotalCount);
