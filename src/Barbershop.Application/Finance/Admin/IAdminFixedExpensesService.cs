namespace Barbershop.Application.Finance.Admin;

public interface IAdminFixedExpensesService
{
    Task<IReadOnlyList<FixedExpenseView>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<FixedExpenseView> GetByIdAsync(Guid fixedExpenseId, CancellationToken cancellationToken = default);

    Task<FixedExpenseView> CreateAsync(FixedExpenseCreateRequest request, CancellationToken cancellationToken = default);

    Task<FixedExpenseView> UpdateAsync(Guid fixedExpenseId, FixedExpenseUpdateRequest request, CancellationToken cancellationToken = default);

    Task<FixedExpenseView> UpdateStatusAsync(Guid fixedExpenseId, FixedExpenseStatusUpdateRequest request, CancellationToken cancellationToken = default);

    Task SoftDeleteAsync(Guid fixedExpenseId, CancellationToken cancellationToken = default);
}
