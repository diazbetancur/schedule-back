namespace Barbershop.Application.Finance.Admin;

public sealed record ExpenseEntryView(
    Guid Id,
    Guid? FixedExpenseId,
    string Name,
    int Amount,
    DateOnly OccurredOn,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record ExpenseEntryCreateRequest(
    Guid? FixedExpenseId,
    string Name,
    int Amount,
    DateOnly OccurredOn);

public sealed record ExpenseEntryUpdateRequest(
    Guid? FixedExpenseId,
    string Name,
    int Amount,
    DateOnly OccurredOn);
