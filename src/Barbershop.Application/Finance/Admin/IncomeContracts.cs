namespace Barbershop.Application.Finance.Admin;

public sealed record IncomeEntryView(
    Guid Id,
    Guid ServiceId,
    string ServiceName,
    int BasePrice,
    Guid StaffProfileId,
    string StaffDisplayName,
    int Amount,
    bool IsPromo,
    int BusinessPercentage,
    int BusinessAmount,
    int ProfessionalAmount,
    DateOnly OccurredOn,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record IncomeEntryCreateRequest(
    Guid ServiceId,
    Guid StaffProfileId,
    int Amount,
    bool IsPromo,
    DateOnly OccurredOn);

public sealed record IncomeEntryUpdateRequest(
    Guid ServiceId,
    Guid StaffProfileId,
    int Amount,
    bool IsPromo,
    DateOnly OccurredOn);
