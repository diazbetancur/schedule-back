using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Finance.Admin;
using Barbershop.Domain.Finance;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Finance;

internal sealed class FixedExpenseManagementService : IAdminFixedExpensesService
{
    private const int NameMinLength = 2;
    private const int NameMaxLength = 120;

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public FixedExpenseManagementService(AppDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<FixedExpenseView>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var items = await _dbContext.FixedExpenses
            .Where(item => !item.IsDeleted)
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);

        return items.Select(Map).ToArray();
    }

    public async Task<FixedExpenseView> GetByIdAsync(Guid fixedExpenseId, CancellationToken cancellationToken = default)
    {
        var item = await LoadAsync(fixedExpenseId, cancellationToken);
        return Map(item);
    }

    public async Task<FixedExpenseView> CreateAsync(FixedExpenseCreateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request.Name, request.DefaultAmount);
        await EnsureNameIsUniqueAsync(request.Name, null, cancellationToken);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var item = new FixedExpense(request.Name, request.DefaultAmount, utcNow);

        _dbContext.FixedExpenses.Add(item);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Map(item);
    }

    public async Task<FixedExpenseView> UpdateAsync(Guid fixedExpenseId, FixedExpenseUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request.Name, request.DefaultAmount);
        var item = await LoadAsync(fixedExpenseId, cancellationToken);
        await EnsureNameIsUniqueAsync(request.Name, fixedExpenseId, cancellationToken);

        item.Update(request.Name, request.DefaultAmount, _timeProvider.GetUtcNow().UtcDateTime);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Map(item);
    }

    public async Task<FixedExpenseView> UpdateStatusAsync(Guid fixedExpenseId, FixedExpenseStatusUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var item = await LoadAsync(fixedExpenseId, cancellationToken);
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        if (request.IsActive)
        {
            item.Activate(utcNow);
        }
        else
        {
            item.Deactivate(utcNow);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Map(item);
    }

    public async Task SoftDeleteAsync(Guid fixedExpenseId, CancellationToken cancellationToken = default)
    {
        var item = await LoadAsync(fixedExpenseId, cancellationToken);
        item.MarkDeleted(_timeProvider.GetUtcNow().UtcDateTime);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<FixedExpense> LoadAsync(Guid fixedExpenseId, CancellationToken cancellationToken)
    {
        return await _dbContext.FixedExpenses
            .SingleOrDefaultAsync(item => item.Id == fixedExpenseId && !item.IsDeleted, cancellationToken)
            ?? throw new KeyNotFoundException("The fixed expense was not found.");
    }

    private async Task EnsureNameIsUniqueAsync(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        var normalized = name.Trim().ToUpperInvariant();
        var duplicateExists = await _dbContext.FixedExpenses.AnyAsync(
            item => !item.IsDeleted
                && item.Id != excludeId
                && item.Name.ToUpper() == normalized,
            cancellationToken);

        if (duplicateExists)
        {
            throw new ConflictException("A fixed expense with this name already exists.");
        }
    }

    private static void ValidateRequest(string name, int? defaultAmount)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length is < NameMinLength or > NameMaxLength)
        {
            errors["name"] = [$"Name must be between {NameMinLength} and {NameMaxLength} characters."];
        }

        if (defaultAmount is < 0)
        {
            errors["defaultAmount"] = ["DefaultAmount must be zero or greater."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }
    }

    private static FixedExpenseView Map(FixedExpense item)
        => new(item.Id, item.Name, item.DefaultAmount, item.IsActive, item.CreatedAt, item.UpdatedAt);
}
