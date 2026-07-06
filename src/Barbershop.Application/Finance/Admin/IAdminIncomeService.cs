namespace Barbershop.Application.Finance.Admin;

public interface IAdminIncomeService
{
    Task<IReadOnlyList<IncomeEntryView>> GetAsync(DateOnly? date, Guid? staffProfileId, CancellationToken cancellationToken = default);

    Task<IncomeEntryView> CreateAsync(Guid currentUserId, IncomeEntryCreateRequest request, CancellationToken cancellationToken = default);

    Task<IncomeEntryView> UpdateAsync(Guid incomeEntryId, IncomeEntryUpdateRequest request, CancellationToken cancellationToken = default);

    Task SoftDeleteAsync(Guid incomeEntryId, CancellationToken cancellationToken = default);
}
