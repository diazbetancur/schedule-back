namespace Barbershop.Application.Finance.Admin;

public interface IAdminExpensesService
{
    Task<IReadOnlyList<ExpenseEntryView>> GetAsync(int? year, int? month, CancellationToken cancellationToken = default);

    Task<ExpenseEntryView> CreateAsync(Guid currentUserId, ExpenseEntryCreateRequest request, CancellationToken cancellationToken = default);

    Task<ExpenseEntryView> UpdateAsync(Guid expenseEntryId, ExpenseEntryUpdateRequest request, CancellationToken cancellationToken = default);

    Task SoftDeleteAsync(Guid expenseEntryId, CancellationToken cancellationToken = default);
}
