namespace Barbershop.Application.Finance.Admin;

public sealed record FixedExpenseView(
    Guid Id,
    string Name,
    int? DefaultAmount,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record FixedExpenseCreateRequest(string Name, int? DefaultAmount);

public sealed record FixedExpenseUpdateRequest(string Name, int? DefaultAmount);

public sealed record FixedExpenseStatusUpdateRequest(bool IsActive);
