using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Finance.Admin;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Services;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Finance;

internal sealed class IncomeManagementService : IAdminIncomeService
{
    // Colombia is a fixed UTC-5 offset (no DST).
    private static readonly TimeSpan ColombiaOffset = TimeSpan.FromHours(-5);

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public IncomeManagementService(AppDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<IncomeEntryView>> GetAsync(DateOnly? date, Guid? staffProfileId, CancellationToken cancellationToken = default)
    {
        var targetDate = date ?? Today();

        var query = _dbContext.IncomeEntries
            .Where(entry => !entry.IsDeleted && entry.OccurredOn == targetDate);

        if (staffProfileId is { } staffId)
        {
            query = query.Where(entry => entry.StaffProfileId == staffId);
        }

        var entries = await query
            .OrderBy(entry => entry.CreatedAt)
            .ToListAsync(cancellationToken);

        return await MapManyAsync(entries, cancellationToken);
    }

    public async Task<IncomeEntryView> CreateAsync(Guid currentUserId, IncomeEntryCreateRequest request, CancellationToken cancellationToken = default)
    {
        var service = await ValidateAndLoadServiceAsync(request.ServiceId, cancellationToken);
        await EnsureStaffExistsAsync(request.StaffProfileId, cancellationToken);
        ValidateAmountAndDate(request.Amount, request.OccurredOn);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var entry = new IncomeEntry(
            service.Id,
            service.Name,
            service.BasePrice,
            request.StaffProfileId,
            request.Amount,
            request.IsPromo,
            request.OccurredOn,
            currentUserId,
            utcNow);

        _dbContext.IncomeEntries.Add(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await MapAsync(entry, cancellationToken);
    }

    public async Task<IncomeEntryView> UpdateAsync(Guid incomeEntryId, IncomeEntryUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var entry = await LoadAsync(incomeEntryId, cancellationToken);
        var service = await ValidateAndLoadServiceAsync(request.ServiceId, cancellationToken);
        await EnsureStaffExistsAsync(request.StaffProfileId, cancellationToken);
        ValidateAmountAndDate(request.Amount, request.OccurredOn);

        entry.Update(
            service.Id,
            service.Name,
            service.BasePrice,
            request.StaffProfileId,
            request.Amount,
            request.IsPromo,
            request.OccurredOn,
            _timeProvider.GetUtcNow().UtcDateTime);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return await MapAsync(entry, cancellationToken);
    }

    public async Task SoftDeleteAsync(Guid incomeEntryId, CancellationToken cancellationToken = default)
    {
        var entry = await LoadAsync(incomeEntryId, cancellationToken);
        entry.MarkDeleted(_timeProvider.GetUtcNow().UtcDateTime);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private DateOnly Today()
        => DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime.Add(ColombiaOffset));

    private async Task<Service> ValidateAndLoadServiceAsync(Guid serviceId, CancellationToken cancellationToken)
    {
        var service = await _dbContext.Services
            .SingleOrDefaultAsync(candidate => candidate.Id == serviceId && !candidate.IsDeleted, cancellationToken);

        if (service is null)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["serviceId"] = ["The selected service does not exist."],
            });
        }

        return service;
    }

    private async Task EnsureStaffExistsAsync(Guid staffProfileId, CancellationToken cancellationToken)
    {
        var exists = await _dbContext.StaffProfiles.AnyAsync(candidate => candidate.Id == staffProfileId, cancellationToken);
        if (!exists)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["staffProfileId"] = ["The selected professional does not exist."],
            });
        }
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

    private async Task<IncomeEntry> LoadAsync(Guid incomeEntryId, CancellationToken cancellationToken)
    {
        return await _dbContext.IncomeEntries
            .SingleOrDefaultAsync(entry => entry.Id == incomeEntryId && !entry.IsDeleted, cancellationToken)
            ?? throw new KeyNotFoundException("The income entry was not found.");
    }

    private async Task<IncomeEntryView> MapAsync(IncomeEntry entry, CancellationToken cancellationToken)
    {
        var displayName = await _dbContext.StaffProfiles
            .Where(staff => staff.Id == entry.StaffProfileId)
            .Select(staff => staff.DisplayName)
            .SingleOrDefaultAsync(cancellationToken) ?? string.Empty;

        return Map(entry, displayName);
    }

    private async Task<IReadOnlyList<IncomeEntryView>> MapManyAsync(IReadOnlyCollection<IncomeEntry> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        var staffIds = entries.Select(entry => entry.StaffProfileId).Distinct().ToArray();
        var displayNames = await _dbContext.StaffProfiles
            .Where(staff => staffIds.Contains(staff.Id))
            .Select(staff => new { staff.Id, staff.DisplayName })
            .ToDictionaryAsync(staff => staff.Id, staff => staff.DisplayName, cancellationToken);

        return entries
            .Select(entry => Map(entry, displayNames.TryGetValue(entry.StaffProfileId, out var name) ? name : string.Empty))
            .ToArray();
    }

    private static IncomeEntryView Map(IncomeEntry entry, string staffDisplayName)
        => new(
            entry.Id,
            entry.ServiceId,
            entry.ServiceNameSnapshot,
            entry.BasePriceSnapshot,
            entry.StaffProfileId,
            staffDisplayName,
            entry.Amount,
            entry.IsPromo,
            entry.OccurredOn,
            entry.CreatedAt,
            entry.UpdatedAt);
}
