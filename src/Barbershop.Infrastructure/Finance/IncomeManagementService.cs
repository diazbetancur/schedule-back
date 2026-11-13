using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Common.Paging;
using Barbershop.Application.Finance.Admin;
using Barbershop.Application.Auth;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Memberships;
using Barbershop.Infrastructure.Memberships;
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
    private readonly ICurrentUserAccessor? _currentUser;

    public IncomeManagementService(AppDbContext dbContext, TimeProvider timeProvider, ICurrentUserAccessor? currentUser = null)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Paginated income history (ADR-0016 B7). The range follows <see cref="HistoryRange"/>: the last 30 days by default
    /// and at most 30 days per query, any period. <c>TotalAmount</c> covers every matching entry, not just the page.
    /// Newest first: date, then registration time, then id (a total order).
    /// </summary>
    public async Task<IncomeEntriesPage> GetAsync(IncomeListQuery query, CancellationToken cancellationToken = default)
    {
        var page = PageRequest.Create(query.Page, query.PageSize);
        var kind = ParseKind(query.Kind);
        var (from, to) = HistoryRange.Resolve(query.From, query.To, Today());

        var entries = _dbContext.IncomeEntries.AsNoTracking()
            .Where(entry => !entry.IsDeleted && entry.OccurredOn >= from && entry.OccurredOn <= to);

        if (query.StaffProfileId is { } staffId)
        {
            entries = entries.Where(entry => entry.StaffProfileId == staffId);
        }

        if (kind is { } wantedKind)
        {
            entries = entries.Where(entry => entry.Kind == wantedKind);
        }

        if (query.GeneralServiceId is { } generalServiceId)
        {
            entries = entries.Where(entry => _dbContext.Services
                .Any(service => service.Id == entry.ServiceId && service.GeneralServiceId == generalServiceId));
        }

        // BUG-080: summed as bigint; an int total overflowed (HTTP 500) once the matching entries passed 2.147.483.647 COP.
        var totalAmount = await entries.SumAsync(entry => (long?)entry.Amount, cancellationToken) ?? 0;
        var result = await entries
            .OrderByDescending(entry => entry.OccurredOn)
            .ThenByDescending(entry => entry.CreatedAt)
            .ThenByDescending(entry => entry.Id)
            .ToPagedResultAsync(page, (rows, ct) => MapManyAsync(rows.ToArray(), ct), cancellationToken);

        return new IncomeEntriesPage(result.Items, result.Page, result.PageSize, result.TotalCount, from, to, totalAmount);
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
        await EnsureNotFromServiceRecordAsync(incomeEntryId, cancellationToken);

        // Omitting the kind keeps the entry's kind, so older clients can still edit service income.
        var requestedKind = ParseKind(request.Kind) ?? entry.Kind;
        if (requestedKind != entry.Kind)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["kind"] = ["El tipo de ingreso no se puede cambiar. Elimina el ingreso y regístralo de nuevo."],
            });
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        if (entry.Kind == IncomeKind.MembershipSale)
        {
            ValidateMembershipSaleShape(request.ServiceId, request.StaffProfileId, request.IsPromo);
            ValidateAmountAndDate(request.Amount, request.OccurredOn);
            return await UpdateMembershipSaleAsync(entry, request, utcNow, cancellationToken);
        }
        else
        {
            var service = await ValidateAndLoadServiceAsync(request.ServiceId, cancellationToken);
            var staffProfileId = await EnsureStaffExistsAsync(request.StaffProfileId, cancellationToken);
            ValidateAmountAndDate(request.Amount, request.OccurredOn);

            // Choosing a different service is the explicit operation that captures its current name, price and
            // split; any other edit keeps the values captured at registration, so later changes to the service
            // master never rewrite the split of an existing income.
            var keepsService = entry.ServiceId == service.Id;

            entry.Update(
                service.Id,
                keepsService ? entry.ServiceNameSnapshot! : service.Name,
                keepsService ? entry.BasePriceSnapshot : service.BasePrice,
                staffProfileId,
                request.Amount,
                request.IsPromo,
                request.OccurredOn,
                utcNow,
                keepsService ? entry.BusinessPercentageSnapshot : service.BusinessPercentage);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return await MapAsync(entry, cancellationToken);
    }

    /// <summary>
    /// BUG-104: changing the amount of a membership sale that already pays for memberships follows the rule of the
    /// link: when the new amount differs from the linked memberships' total a justification is required, and the change
    /// is recorded in each membership's history with it. Under the income row lock the link takes, so a link committing
    /// at the same time is seen here.
    /// </summary>
    private async Task<IncomeEntryView> UpdateMembershipSaleAsync(
        IncomeEntry entry, IncomeEntryUpdateRequest request, DateTime utcNow, CancellationToken cancellationToken)
    {
        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        if (_dbContext.Database.IsNpgsql())
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM public.income_entries WHERE \"Id\" = {entry.Id} FOR UPDATE", cancellationToken);
            await _dbContext.Entry(entry).ReloadAsync(cancellationToken);
        }

        var previousAmount = entry.Amount;
        if (request.Amount != previousAmount)
        {
            var linked = await _dbContext.CustomerMemberships
                .Where(membership => membership.IncomeEntryId == entry.Id)
                .ToListAsync(cancellationToken);
            if (linked.Count > 0)
            {
                var justification = MembershipSalesService.RequireJustificationIfMismatch(
                    request.Amount, linked.Sum(membership => (long)membership.Price), request.DiscrepancyJustification);
                var changedBy = _currentUser?.UserId
                    ?? throw new InvalidOperationException("Inicia sesión de nuevo para cambiar este ingreso.");
                var reason = $"Valor del ingreso cambiado de {previousAmount:N0} a {request.Amount:N0}."
                    + (justification is null ? string.Empty : $" {justification}");
                foreach (var membership in linked)
                {
                    _dbContext.MembershipEvents.Add(new MembershipEvent(
                        membership.Id, MembershipEventType.IncomeAmountChanged, changedBy, utcNow,
                        reason.Length <= 500 ? reason : reason[..500], entry.Id));
                }
            }
        }

        entry.UpdateMembershipSale(request.Amount, request.OccurredOn, utcNow);
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return await MapAsync(entry, cancellationToken);
    }

    public async Task SoftDeleteAsync(Guid incomeEntryId, CancellationToken cancellationToken = default)
    {
        var entry = await LoadAsync(incomeEntryId, cancellationToken);
        await EnsureNotFromServiceRecordAsync(incomeEntryId, cancellationToken);

        // Memberships point to the income that paid for them; deleting it would leave them pointing at nothing.
        if (await _dbContext.CustomerMemberships.AnyAsync(membership => membership.IncomeEntryId == incomeEntryId, cancellationToken))
        {
            throw new ConflictException("Este ingreso está vinculado a una o más membresías. Desvincúlalas antes de eliminarlo.");
        }

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
                ["kind"] = ["El tipo de ingreso debe ser Servicio o Venta de membresía."],
            })
            : Enum.Parse<IncomeKind>(name);
    }

    /// <summary>A membership sale has no service, no professional and no promo flag.</summary>
    private static void ValidateMembershipSaleShape(Guid? serviceId, Guid? staffProfileId, bool isPromo)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (serviceId is not null)
        {
            errors["serviceId"] = ["Una venta de membresía no lleva servicio."];
        }

        if (staffProfileId is not null)
        {
            errors["staffProfileId"] = ["Una venta de membresía no lleva profesional."];
        }

        if (isPromo)
        {
            errors["isPromo"] = ["Una venta de membresía no se puede marcar como promoción."];
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
                ["serviceId"] = ["Elige el servicio del ingreso."],
            });
        }

        var service = await _dbContext.Services
            .SingleOrDefaultAsync(candidate => candidate.Id == serviceId && !candidate.IsDeleted, cancellationToken);

        if (service is null)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["serviceId"] = ["El servicio elegido no existe."],
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
                ["staffProfileId"] = ["Elige el profesional del ingreso."],
            });
        }

        var exists = await _dbContext.StaffProfiles.AnyAsync(candidate => candidate.Id == staffId, cancellationToken);
        if (!exists)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["staffProfileId"] = ["El profesional elegido no existe."],
            });
        }

        return staffId;
    }

    private void ValidateAmountAndDate(int amount, DateOnly occurredOn)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (amount < 0)
        {
            errors["amount"] = ["El valor debe ser cero o mayor."];
        }

        if (occurredOn > Today())
        {
            errors["occurredOn"] = ["La fecha no puede ser futura."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }
    }

    /// <summary>
    /// An income created by confirming a service record belongs to that record: editing or deleting it here would
    /// silently change a confirmed operation. How a confirmed record is corrected is still to be decided.
    /// </summary>
    private async Task EnsureNotFromServiceRecordAsync(Guid incomeEntryId, CancellationToken cancellationToken)
    {
        if (await _dbContext.ServiceRecords.AnyAsync(record => record.IncomeEntryId == incomeEntryId, cancellationToken))
        {
            throw new ConflictException(
                "Este ingreso proviene de un servicio realizado confirmado; no se puede editar ni eliminar desde Ingresos.");
        }
    }

    private async Task<IncomeEntry> LoadAsync(Guid incomeEntryId, CancellationToken cancellationToken)
    {
        return await _dbContext.IncomeEntries
            .SingleOrDefaultAsync(entry => entry.Id == incomeEntryId && !entry.IsDeleted, cancellationToken)
            ?? throw new KeyNotFoundException("No encontramos el ingreso. Es posible que otra persona lo haya eliminado.");
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

        var serviceRecordId = await _dbContext.ServiceRecords
            .Where(record => record.IncomeEntryId == entry.Id)
            .Select(record => (Guid?)record.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return Map(entry, displayName, serviceRecordId);
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

        var entryIds = entries.Select(entry => (Guid?)entry.Id).ToArray();
        var serviceRecordIds = await _dbContext.ServiceRecords
            .Where(record => entryIds.Contains(record.IncomeEntryId))
            .Select(record => new { IncomeEntryId = record.IncomeEntryId!.Value, record.Id })
            .ToDictionaryAsync(record => record.IncomeEntryId, record => record.Id, cancellationToken);

        return entries
            .Select(entry => Map(
                entry,
                entry.StaffProfileId is { } staffId ? displayNames.GetValueOrDefault(staffId, string.Empty) : null,
                serviceRecordIds.TryGetValue(entry.Id, out var serviceRecordId) ? serviceRecordId : null))
            .ToArray();
    }

    /// <summary>A membership sale has no professional, so its display name is null rather than empty.</summary>
    private static IncomeEntryView Map(IncomeEntry entry, string? staffDisplayName, Guid? serviceRecordId)
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
            entry.Kind.ToString(),
            serviceRecordId);
}
