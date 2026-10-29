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
        var kind = ParseKind(request.Kind) ?? IncomeKind.Service;
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        IncomeEntry entry;

        if (kind == IncomeKind.MembershipSale)
        {
            ValidateMembershipSaleShape(request.ServiceId, request.StaffProfileId, request.IsPromo);
            ValidateAmountAndDate(request.Amount, request.OccurredOn);
            entry = IncomeEntry.CreateMembershipSale(request.Amount, request.OccurredOn, currentUserId, utcNow);
        }
        else
        {
            var service = await ValidateAndLoadServiceAsync(request.ServiceId, cancellationToken);
            var staffProfileId = await EnsureStaffExistsAsync(request.StaffProfileId, cancellationToken);
            ValidateAmountAndDate(request.Amount, request.OccurredOn);

            entry = new IncomeEntry(
                service.Id,
                service.Name,
                service.BasePrice,
                staffProfileId,
                request.Amount,
                request.IsPromo,
                request.OccurredOn,
                currentUserId,
                utcNow,
                service.BusinessPercentage);
        }

        _dbContext.IncomeEntries.Add(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await MapAsync(entry, cancellationToken);
    }

    public async Task<IncomeEntryView> UpdateAsync(Guid incomeEntryId, IncomeEntryUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var entry = await LoadAsync(incomeEntryId, cancellationToken);

        // Omitting the kind keeps the entry's kind, so older clients can still edit service income.
        var requestedKind = ParseKind(request.Kind) ?? entry.Kind;
        if (requestedKind != entry.Kind)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["kind"] = ["The income type cannot be changed. Delete the entry and register it again."],
            });
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        if (entry.Kind == IncomeKind.MembershipSale)
        {
            ValidateMembershipSaleShape(request.ServiceId, request.StaffProfileId, request.IsPromo);
            ValidateAmountAndDate(request.Amount, request.OccurredOn);
            entry.UpdateMembershipSale(request.Amount, request.OccurredOn, utcNow);
        }
        else
        {
            var service = await ValidateAndLoadServiceAsync(request.ServiceId, cancellationToken);
            var staffProfileId = await EnsureStaffExistsAsync(request.StaffProfileId, cancellationToken);
            ValidateAmountAndDate(request.Amount, request.OccurredOn);

            entry.Update(
                service.Id,
                service.Name,
                service.BasePrice,
                staffProfileId,
                request.Amount,
                request.IsPromo,
                request.OccurredOn,
                utcNow,
                service.BusinessPercentage);
        }

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

    /// <summary>Null when omitted (callers pick the default); only the enum names are accepted.</summary>
    private static IncomeKind? ParseKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return null;
        }

        var name = Enum.GetNames<IncomeKind>().FirstOrDefault(candidate =>
            string.Equals(candidate, kind.Trim(), StringComparison.OrdinalIgnoreCase));

        return name is null
            ? throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["kind"] = ["Kind must be Service or MembershipSale."],
            })
            : Enum.Parse<IncomeKind>(name);
    }

    /// <summary>A membership sale has no service, no professional and no promo flag.</summary>
    private static void ValidateMembershipSaleShape(Guid? serviceId, Guid? staffProfileId, bool isPromo)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (serviceId is not null)
        {
            errors["serviceId"] = ["A membership sale is not tied to a service."];
        }

        if (staffProfileId is not null)
        {
            errors["staffProfileId"] = ["A membership sale is not tied to a professional."];
        }

        if (isPromo)
        {
            errors["isPromo"] = ["A membership sale cannot be marked as promo."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }
    }

    private async Task<Service> ValidateAndLoadServiceAsync(Guid? serviceId, CancellationToken cancellationToken)
    {
        if (serviceId is null)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["serviceId"] = ["A service is required for service income."],
            });
        }

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

    private async Task<Guid> EnsureStaffExistsAsync(Guid? staffProfileId, CancellationToken cancellationToken)
    {
        if (staffProfileId is not { } staffId)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["staffProfileId"] = ["A professional is required for service income."],
            });
        }

        var exists = await _dbContext.StaffProfiles.AnyAsync(candidate => candidate.Id == staffId, cancellationToken);
        if (!exists)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["staffProfileId"] = ["The selected professional does not exist."],
            });
        }

        return staffId;
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
        string? displayName = null;
        if (entry.StaffProfileId is { } staffId)
        {
            displayName = await _dbContext.StaffProfiles
                .Where(staff => staff.Id == staffId)
                .Select(staff => staff.DisplayName)
                .SingleOrDefaultAsync(cancellationToken) ?? string.Empty;
        }

        return Map(entry, displayName);
    }

    private async Task<IReadOnlyList<IncomeEntryView>> MapManyAsync(IReadOnlyCollection<IncomeEntry> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        var staffIds = entries
            .Where(entry => entry.StaffProfileId is not null)
            .Select(entry => entry.StaffProfileId!.Value)
            .Distinct()
            .ToArray();
        var displayNames = staffIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await _dbContext.StaffProfiles
                .Where(staff => staffIds.Contains(staff.Id))
                .Select(staff => new { staff.Id, staff.DisplayName })
                .ToDictionaryAsync(staff => staff.Id, staff => staff.DisplayName, cancellationToken);

        return entries
            .Select(entry => Map(
                entry,
                entry.StaffProfileId is { } staffId ? displayNames.GetValueOrDefault(staffId, string.Empty) : null))
            .ToArray();
    }

    /// <summary>A membership sale has no professional, so its display name is null rather than empty.</summary>
    private static IncomeEntryView Map(IncomeEntry entry, string? staffDisplayName)
        => new(
            entry.Id,
            entry.ServiceId,
            entry.ServiceNameSnapshot,
            entry.BasePriceSnapshot,
            entry.StaffProfileId,
            staffDisplayName,
            entry.Amount,
            entry.IsPromo,
            entry.BusinessPercentageSnapshot,
            entry.BusinessAmount,
            entry.ProfessionalAmount,
            entry.OccurredOn,
            entry.CreatedAt,
            entry.UpdatedAt,
            entry.Kind.ToString());
}
