using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Finance.Admin;
using Barbershop.Domain.Finance;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Finance;

internal sealed class ExpenseManagementService : IAdminExpensesService
{
    private const int NameMinLength = 2;
    private const int NameMaxLength = 120;

    // Colombia is a fixed UTC-5 offset (no DST).
    private static readonly TimeSpan ColombiaOffset = TimeSpan.FromHours(-5);

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public ExpenseManagementService(AppDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<ExpenseEntryView>> GetAsync(int? year, int? month, CancellationToken cancellationToken = default)
    {
        var today = Today();
        var targetYear = year is >= 1 and <= 9999 ? year.Value : today.Year;
        var targetMonth = month is >= 1 and <= 12 ? month.Value : today.Month;

        var first = new DateOnly(targetYear, targetMonth, 1);
        var firstOfNextMonth = first.AddMonths(1);

        var entries = await _dbContext.ExpenseEntries
            .Where(entry => !entry.IsDeleted && entry.OccurredOn >= first && entry.OccurredOn < firstOfNextMonth)
            .OrderByDescending(entry => entry.OccurredOn)
            .ThenBy(entry => entry.CreatedAt)
            .ToListAsync(cancellationToken);

        return entries.Select(Map).ToArray();
    }

    public async Task<ExpenseEntryView> CreateAsync(Guid currentUserId, ExpenseEntryCreateRequest request, CancellationToken cancellationToken = default)
    {
        var name = await ResolveNameAsync(request.FixedExpenseId, request.Name, cancellationToken);
        ValidateAmountAndDate(request.Amount, request.OccurredOn);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var entry = new ExpenseEntry(request.FixedExpenseId, name, request.Amount, request.OccurredOn, currentUserId, utcNow);

        _dbContext.ExpenseEntries.Add(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Map(entry);
    }

    public async Task<ExpenseEntryView> UpdateAsync(Guid expenseEntryId, ExpenseEntryUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var entry = await LoadAsync(expenseEntryId, cancellationToken);
        var name = await ResolveNameAsync(request.FixedExpenseId, request.Name, cancellationToken);
        ValidateAmountAndDate(request.Amount, request.OccurredOn);

        entry.Update(request.FixedExpenseId, name, request.Amount, request.OccurredOn, _timeProvider.GetUtcNow().UtcDateTime);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Map(entry);
    }

    public async Task SoftDeleteAsync(Guid expenseEntryId, CancellationToken cancellationToken = default)
    {
        var entry = await LoadAsync(expenseEntryId, cancellationToken);
        entry.MarkDeleted(_timeProvider.GetUtcNow().UtcDateTime);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private DateOnly Today()
        => DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime.Add(ColombiaOffset));

    // If a fixed expense is referenced, its current name is the snapshot; otherwise the typed ad-hoc name is validated and used.
    private async Task<string> ResolveNameAsync(Guid? fixedExpenseId, string requestName, CancellationToken cancellationToken)
    {
        if (fixedExpenseId is { } id)
        {
            var fixedExpense = await _dbContext.FixedExpenses
                .SingleOrDefaultAsync(item => item.Id == id && !item.IsDeleted, cancellationToken);

            if (fixedExpense is null)
            {
                throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["fixedExpenseId"] = ["The selected fixed expense does not exist."],
                });
            }

            return fixedExpense.Name;
        }

        if (string.IsNullOrWhiteSpace(requestName) || requestName.Trim().Length is < NameMinLength or > NameMaxLength)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["name"] = [$"Name must be between {NameMinLength} and {NameMaxLength} characters."],
            });
        }

        return requestName.Trim();
    }

    private void ValidateAmountAndDate(int amount, DateOnly occurredOn)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (amount < 0)
        {
            errors["amount"] = ["Amount must be zero or greater."];
        }

        if (occurredOn > Today())
        {
            errors["occurredOn"] = ["OccurredOn cannot be in the future."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }
    }

    private async Task<ExpenseEntry> LoadAsync(Guid expenseEntryId, CancellationToken cancellationToken)
    {
        return await _dbContext.ExpenseEntries
            .SingleOrDefaultAsync(entry => entry.Id == expenseEntryId && !entry.IsDeleted, cancellationToken)
            ?? throw new KeyNotFoundException("The expense entry was not found.");
    }

    private static ExpenseEntryView Map(ExpenseEntry entry)
        => new(entry.Id, entry.FixedExpenseId, entry.Name, entry.Amount, entry.OccurredOn, entry.CreatedAt, entry.UpdatedAt);
}
