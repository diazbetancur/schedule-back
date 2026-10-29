using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Domain.Common;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Memberships;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Memberships;

internal sealed class MembershipSalesService : IAdminMembershipsService
{
    private const int ReasonMinLength = 3;
    private const int ReasonMaxLength = 500;

    private const string ConcurrencyConflictMessage =
        "La membresía o el cliente fueron modificados por otra operación (por ejemplo, otra venta en curso). Recarga e intenta de nuevo.";

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public MembershipSalesService(AppDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<CustomerMembershipSummaryView>> GetCustomerMembershipsAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        if (!await _dbContext.Customers.AnyAsync(customer => customer.Id == customerId, cancellationToken))
        {
            throw new NotFoundException("El cliente no existe.");
        }

        var memberships = await _dbContext.CustomerMemberships
            .Include(membership => membership.Allowances)
            .Include(membership => membership.Gifts)
            .Where(membership => membership.CustomerId == customerId)
            .OrderByDescending(membership => membership.CreatedAt)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        var today = Today();
        return memberships.Select(membership => new CustomerMembershipSummaryView(
                membership.Id,
                membership.CustomerId,
                membership.PlanName,
                membership.Price,
                membership.Mode.ToString(),
                membership.Status.ToString(),
                DisplayStatus(membership, today),
                membership.SoldOn,
                membership.StartsOn,
                membership.EndsOn,
                membership.Allowances.Where(line => line.Origin == MembershipAllowanceOrigin.Purchased).Sum(line => line.GrantedQuantity),
                membership.Allowances.Where(line => line.Origin == MembershipAllowanceOrigin.Gift).Sum(line => line.GrantedQuantity),
                membership.Gifts.Count(gift => gift.Kind == MembershipGiftKind.Physical),
                membership.IncomeEntryId,
                membership.CreatedAt))
            .ToArray();
    }

    public async Task<CustomerMembershipDetailView> GetByIdAsync(Guid membershipId, CancellationToken cancellationToken = default)
    {
        var membership = await LoadAsync(membershipId, cancellationToken);
        return await MapDetailAsync(membership, cancellationToken);
    }

    public async Task<CustomerMembershipDetailView> SellAsync(
        Guid currentUserId,
        Guid customerId,
        MembershipSaleRequest request,
        CancellationToken cancellationToken = default)
    {
        var customer = await _dbContext.Customers.SingleOrDefaultAsync(candidate => candidate.Id == customerId, cancellationToken)
            ?? throw new NotFoundException("El cliente no existe.");
        if (customer.IsMerged)
        {
            throw new ConflictException("El cliente fue fusionado en otro perfil; vende la membresía al perfil que se conserva.");
        }

        var plan = await _dbContext.MembershipPlans
            .Include(candidate => candidate.Services)
            .Include(candidate => candidate.GiftRules)
            .AsSplitQuery()
            .SingleOrDefaultAsync(candidate => candidate.Id == request.PlanId && !candidate.IsDeleted, cancellationToken);

        var today = Today();
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (plan is null)
        {
            errors["planId"] = ["El tipo de membresía no existe."];
        }
        else if (!plan.IsActive)
        {
            errors["planId"] = ["El tipo de membresía está inactivo y no se puede vender."];
        }

        var soldOn = request.SoldOn ?? today;
        if (soldOn > today)
        {
            errors["soldOn"] = ["La fecha de venta no puede ser futura."];
        }

        if (request.StartsOn == default)
        {
            errors["startsOn"] = ["Indica la fecha de inicio de la vigencia."];
        }

        var endsOn = request.EndsOn ?? (plan is not null && request.StartsOn != default
            ? request.StartsOn.AddDays(plan.DefaultDurationDays - 1)
            : default);
        if (request.StartsOn != default && endsOn < request.StartsOn)
        {
            errors["endsOn"] = ["La fecha de vencimiento no puede ser anterior a la de inicio."];
        }
        else if (request.StartsOn != default && endsOn.DayNumber - request.StartsOn.DayNumber + 1 > MembershipPlan.MaxDurationDays)
        {
            errors["endsOn"] = [$"La vigencia no puede superar {MembershipPlan.MaxDurationDays} días."];
        }

        string? activationReason = null;
        if (request.ActivateNow && request.StartsOn != default && endsOn < today)
        {
            activationReason = ValidateExpiredActivation(request.ConfirmExpiredActivation, request.ActivationReason, errors, "activationReason");
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        // First purchase is per plan, and annulled memberships do not count as a purchase.
        var hasPreviousPurchase = await _dbContext.CustomerMemberships.AnyAsync(
            membership => membership.CustomerId == customer.Id
                && membership.MembershipPlanId == plan!.Id
                && membership.Status != MembershipStatus.Annulled,
            cancellationToken);

        var serviceNames = await LoadServiceNamesAsync(
            plan!.Services.Select(service => service.ServiceId)
                .Concat(plan.GiftRules.Where(rule => rule.ServiceId is not null).Select(rule => rule.ServiceId!.Value)),
            cancellationToken);

        var membership = new CustomerMembership(customer.Id, plan, soldOn, request.StartsOn, endsOn, currentUserId, utcNow);
        AddPurchasedAllowances(membership, plan, serviceNames);
        AddGifts(membership, plan, hasPreviousPurchase, serviceNames, utcNow);

        _dbContext.CustomerMemberships.Add(membership);
        _dbContext.MembershipEvents.Add(new MembershipEvent(membership.Id, MembershipEventType.Sold, currentUserId, utcNow));

        if (request.ActivateNow)
        {
            Activate(membership, currentUserId, activationReason, utcNow);
        }

        // Writing the customer row makes two concurrent sales to the same customer collide on its concurrency
        // token, so a first-purchase gift can never be granted twice.
        customer.Touch(utcNow);

        await SaveChangesAsync(cancellationToken);
        return await MapDetailAsync(membership, cancellationToken);
    }

    public async Task<CustomerMembershipDetailView> ActivateAsync(
        Guid currentUserId,
        Guid membershipId,
        MembershipActivateRequest request,
        CancellationToken cancellationToken = default)
    {
        var membership = await LoadAsync(membershipId, cancellationToken);
        if (membership.Status == MembershipStatus.Annulled)
        {
            throw new ConflictException("La membresía está anulada y no se puede activar.");
        }

        if (membership.Status == MembershipStatus.Active)
        {
            throw new ConflictException("La membresía ya está activa.");
        }

        string? reason = null;
        if (membership.IsExpiredOn(Today()))
        {
            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            reason = ValidateExpiredActivation(request.ConfirmExpiredActivation, request.Reason, errors, "reason");
            if (errors.Count > 0)
            {
                throw new ValidationProblemException(errors);
            }
        }

        Activate(membership, currentUserId, reason, _timeProvider.GetUtcNow().UtcDateTime);
        await SaveChangesAsync(cancellationToken);
        return await MapDetailAsync(membership, cancellationToken);
    }

    public async Task<CustomerMembershipDetailView> AnnulAsync(
        Guid currentUserId,
        Guid membershipId,
        MembershipAnnulRequest request,
        CancellationToken cancellationToken = default)
    {
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < ReasonMinLength or > ReasonMaxLength)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["reason"] = [$"El motivo debe tener entre {ReasonMinLength} y {ReasonMaxLength} caracteres."]
            });
        }

        var membership = await LoadAsync(membershipId, cancellationToken);
        if (membership.Status == MembershipStatus.Annulled)
        {
            throw new ConflictException("La membresía ya está anulada.");
        }

        // Annulment blocks the pending benefits (enforced where they are used); nothing is deleted or refunded.
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        membership.Annul(currentUserId, reason, utcNow);
        _dbContext.MembershipEvents.Add(new MembershipEvent(membership.Id, MembershipEventType.Annulled, currentUserId, utcNow, reason));

        await SaveChangesAsync(cancellationToken);
        return await MapDetailAsync(membership, cancellationToken);
    }

    public async Task<IReadOnlyList<MembershipIncomeCandidateView>> GetIncomeCandidatesAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var incomes = await _dbContext.IncomeEntries
            .Where(entry => !entry.IsDeleted && entry.Kind == IncomeKind.MembershipSale && entry.OccurredOn == date)
            .OrderBy(entry => entry.CreatedAt)
            .ToListAsync(cancellationToken);

        var peers = await LoadLinkedPeersAsync(incomes.Select(entry => entry.Id), cancellationToken);
        return incomes.Select(entry =>
            {
                var linked = peers.GetValueOrDefault(entry.Id) ?? [];
                return new MembershipIncomeCandidateView(entry.Id, entry.Amount, entry.OccurredOn, linked.Sum(peer => peer.Price), linked);
            })
            .ToArray();
    }

    public async Task<CustomerMembershipDetailView> LinkIncomeAsync(
        Guid currentUserId,
        Guid membershipId,
        MembershipIncomeLinkRequest request,
        CancellationToken cancellationToken = default)
    {
        var membershipIds = (request.AdditionalMembershipIds ?? [])
            .Prepend(membershipId)
            .Distinct()
            .ToArray();
        var toLink = await _dbContext.CustomerMemberships
            .Where(candidate => membershipIds.Contains(candidate.Id))
            .ToListAsync(cancellationToken);
        if (toLink.Count != membershipIds.Length)
        {
            throw new NotFoundException("Alguna de las membresías no existe.");
        }

        if (toLink.Any(candidate => candidate.IncomeEntryId is not null))
        {
            throw new ConflictException("Alguna de las membresías ya tiene un ingreso vinculado. Desvincúlalo antes de vincular otro.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await LockIncomeAsync(request.IncomeEntryId, cancellationToken);

        var income = await _dbContext.IncomeEntries
            .SingleOrDefaultAsync(entry => entry.Id == request.IncomeEntryId && !entry.IsDeleted, cancellationToken);
        if (income is null || income.Kind != IncomeKind.MembershipSale)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["incomeEntryId"] = [income is null
                    ? "El ingreso no existe."
                    : "Solo se pueden vincular ingresos registrados como venta de membresía."]
            });
        }

        var alreadyLinkedTotal = await _dbContext.CustomerMemberships
            .Where(other => other.IncomeEntryId == income.Id)
            .SumAsync(other => other.Price, cancellationToken);
        var justification = RequireJustificationIfMismatch(
            income.Amount, alreadyLinkedTotal + toLink.Sum(candidate => candidate.Price), request.DiscrepancyJustification);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var candidate in toLink)
        {
            candidate.LinkIncome(income.Id, utcNow);
            _dbContext.MembershipEvents.Add(new MembershipEvent(
                candidate.Id, MembershipEventType.IncomeLinked, currentUserId, utcNow, justification, income.Id));
        }

        await SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await MapDetailAsync(await LoadAsync(membershipId, cancellationToken), cancellationToken);
    }

    public async Task<CustomerMembershipDetailView> UnlinkIncomeAsync(
        Guid currentUserId,
        Guid membershipId,
        MembershipIncomeUnlinkRequest request,
        CancellationToken cancellationToken = default)
    {
        var membership = await LoadAsync(membershipId, cancellationToken);
        if (membership.IncomeEntryId is not { } incomeEntryId)
        {
            throw new ConflictException("La membresía no tiene un ingreso vinculado.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await LockIncomeAsync(incomeEntryId, cancellationToken);

        var income = await _dbContext.IncomeEntries.SingleAsync(entry => entry.Id == incomeEntryId, cancellationToken);
        var remaining = await _dbContext.CustomerMemberships
            .Where(other => other.IncomeEntryId == incomeEntryId && other.Id != membership.Id)
            .Select(other => other.Price)
            .ToListAsync(cancellationToken);

        // The memberships left on the income must still add up to it, or the difference is explained.
        var justification = remaining.Count > 0 && !income.IsDeleted
            ? RequireJustificationIfMismatch(income.Amount, remaining.Sum(), request.DiscrepancyJustification)
            : NormalizeOptionalReason(request.DiscrepancyJustification);

        // Unlinking only drops the reference: balances, gifts and history stay as they are.
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        membership.UnlinkIncome(utcNow);
        _dbContext.MembershipEvents.Add(new MembershipEvent(
            membership.Id, MembershipEventType.IncomeUnlinked, currentUserId, utcNow, justification, incomeEntryId));

        await SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await MapDetailAsync(membership, cancellationToken);
    }

    private static void AddPurchasedAllowances(
        CustomerMembership membership,
        MembershipPlan plan,
        IReadOnlyDictionary<Guid, string> serviceNames)
    {
        var services = plan.Services
            .OrderBy(service => serviceNames.GetValueOrDefault(service.ServiceId, string.Empty), StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (plan.Mode == MembershipPlanMode.Pool)
        {
            // One shared balance usable by every included service.
            var pool = new MembershipAllowance(membership.Id, MembershipAllowanceOrigin.Purchased, plan.PoolQuantity!.Value, position: 0);
            foreach (var service in services)
            {
                pool.Services.Add(new MembershipAllowanceService(pool.Id, service.ServiceId, serviceNames[service.ServiceId]));
            }

            membership.Allowances.Add(pool);
            return;
        }

        for (var position = 0; position < services.Count; position++)
        {
            var service = services[position];
            var line = new MembershipAllowance(membership.Id, MembershipAllowanceOrigin.Purchased, service.Quantity!.Value, position);
            line.Services.Add(new MembershipAllowanceService(line.Id, service.ServiceId, serviceNames[service.ServiceId]));
            membership.Allowances.Add(line);
        }
    }

    private static void AddGifts(
        CustomerMembership membership,
        MembershipPlan plan,
        bool hasPreviousPurchase,
        IReadOnlyDictionary<Guid, string> serviceNames,
        DateTime utcNow)
    {
        var position = membership.Allowances.Count;
        foreach (var rule in plan.GiftRules.Where(rule => !rule.IsDeleted).OrderBy(rule => rule.Position))
        {
            if (rule.Condition == MembershipGiftCondition.FirstPurchaseOfPlan && hasPreviousPurchase)
            {
                continue;
            }

            var serviceName = rule.ServiceId is { } serviceId ? serviceNames[serviceId] : null;
            var gift = new MembershipGift(membership.Id, rule, serviceName, utcNow);
            membership.Gifts.Add(gift);

            if (rule.Kind == MembershipGiftKind.Service)
            {
                // Gifted units get their own balance line, separate from what was purchased.
                var line = new MembershipAllowance(membership.Id, MembershipAllowanceOrigin.Gift, rule.Quantity, position++, gift.Id);
                line.Services.Add(new MembershipAllowanceService(line.Id, rule.ServiceId!.Value, serviceName!));
                membership.Allowances.Add(line);
            }
        }
    }

    private void Activate(CustomerMembership membership, Guid currentUserId, string? expiredActivationReason, DateTime utcNow)
    {
        membership.Activate(currentUserId, utcNow);
        _dbContext.MembershipEvents.Add(expiredActivationReason is null
            ? new MembershipEvent(membership.Id, MembershipEventType.Activated, currentUserId, utcNow)
            : new MembershipEvent(membership.Id, MembershipEventType.ActivatedAfterExpiry, currentUserId, utcNow, expiredActivationReason));
    }

    private static string? ValidateExpiredActivation(bool confirmed, string? reason, Dictionary<string, string[]> errors, string reasonKey)
    {
        if (!confirmed)
        {
            errors["confirmExpiredActivation"] = ["La membresía ya venció: confirma que quieres activarla de forma excepcional."];
        }

        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length is < ReasonMinLength or > ReasonMaxLength)
        {
            errors[reasonKey] = [$"Explica por qué se activa una membresía vencida (entre {ReasonMinLength} y {ReasonMaxLength} caracteres)."];
        }

        return trimmed;
    }

    private static string? RequireJustificationIfMismatch(int incomeAmount, int membershipsTotal, string? justification)
    {
        var normalized = NormalizeOptionalReason(justification);
        if (incomeAmount == membershipsTotal)
        {
            return normalized;
        }

        if (normalized is null || normalized.Length < ReasonMinLength)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["discrepancyJustification"] =
                [
                    $"El valor del ingreso ({incomeAmount:N0}) no coincide con la suma de las membresías vinculadas ({membershipsTotal:N0}). Explica la diferencia."
                ]
            });
        }

        return normalized;
    }

    private static string? NormalizeOptionalReason(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > ReasonMaxLength)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["discrepancyJustification"] = [$"La justificación debe tener {ReasonMaxLength} caracteres o menos."]
            });
        }

        return trimmed;
    }

    /// <summary>
    /// Serializes link/unlink operations on one income (the discrepancy check sums every membership on it).
    /// Uses a PostgreSQL row lock so the finance table needs no schema change; other providers skip it.
    /// </summary>
    private async Task LockIncomeAsync(Guid incomeEntryId, CancellationToken cancellationToken)
    {
        if (_dbContext.Database.IsNpgsql())
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM public.income_entries WHERE \"Id\" = {incomeEntryId} FOR UPDATE",
                cancellationToken);
        }
    }

    private async Task<CustomerMembership> LoadAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        return await _dbContext.CustomerMemberships
            .Include(membership => membership.Allowances)
            .ThenInclude(allowance => allowance.Services)
            .Include(membership => membership.Gifts)
            .AsSplitQuery()
            .SingleOrDefaultAsync(membership => membership.Id == membershipId, cancellationToken)
            ?? throw new NotFoundException("La membresía no existe.");
    }

    private async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(ConcurrencyConflictMessage);
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(BogotaClock.ToLocal(_timeProvider.GetUtcNow().UtcDateTime));

    private static string DisplayStatus(CustomerMembership membership, DateOnly today) => membership.Status switch
    {
        MembershipStatus.Annulled => "Annulled",
        MembershipStatus.PendingActivation => "PendingActivation",
        _ when today < membership.StartsOn => "Scheduled",
        _ when membership.IsExpiredOn(today) => "Expired",
        _ => "Active",
    };

    private async Task<Dictionary<Guid, string>> LoadServiceNamesAsync(IEnumerable<Guid> serviceIds, CancellationToken cancellationToken)
    {
        var ids = serviceIds.Distinct().ToArray();
        return await _dbContext.Services
            .Where(service => ids.Contains(service.Id))
            .ToDictionaryAsync(service => service.Id, service => service.Name, cancellationToken);
    }

    private async Task<Dictionary<Guid, IReadOnlyList<MembershipLinkedPeerView>>> LoadLinkedPeersAsync(
        IEnumerable<Guid> incomeEntryIds,
        CancellationToken cancellationToken)
    {
        var ids = incomeEntryIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var linked = await (
                from membership in _dbContext.CustomerMemberships
                join customer in _dbContext.Customers on membership.CustomerId equals customer.Id
                where membership.IncomeEntryId != null && ids.Contains(membership.IncomeEntryId.Value)
                orderby membership.CreatedAt
                select new
                {
                    IncomeEntryId = membership.IncomeEntryId!.Value,
                    membership.Id,
                    customer.FullName,
                    membership.PlanName,
                    membership.Price,
                    membership.Status,
                })
            .ToListAsync(cancellationToken);

        return linked
            .GroupBy(row => row.IncomeEntryId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<MembershipLinkedPeerView>)group
                    .Select(row => new MembershipLinkedPeerView(row.Id, row.FullName, row.PlanName, row.Price, row.Status.ToString()))
                    .ToArray());
    }

    private async Task<CustomerMembershipDetailView> MapDetailAsync(CustomerMembership membership, CancellationToken cancellationToken)
    {
        var events = (await _dbContext.MembershipEvents
                .Where(membershipEvent => membershipEvent.CustomerMembershipId == membership.Id)
                .ToListAsync(cancellationToken))
            .OrderByDescending(membershipEvent => membershipEvent.PerformedAt)
            .ThenByDescending(membershipEvent => membershipEvent.Type)
            .ThenByDescending(membershipEvent => membershipEvent.Id)
            .ToList();

        var movements = await _dbContext.MembershipMovements
            .Where(movement => movement.CustomerMembershipId == membership.Id)
            .ToListAsync(cancellationToken);
        var consumptions = await _dbContext.MembershipConsumptions
            .Where(consumption => consumption.CustomerMembershipId == membership.Id)
            .ToListAsync(cancellationToken);
        var consumptionIds = consumptions.Select(consumption => consumption.Id).ToArray();
        var reversals = await _dbContext.MembershipConsumptionReversals
            .Where(reversal => consumptionIds.Contains(reversal.MembershipConsumptionId))
            .ToDictionaryAsync(reversal => reversal.MembershipConsumptionId, cancellationToken);
        var staffIds = consumptions.Where(consumption => consumption.StaffProfileId is not null)
            .Select(consumption => consumption.StaffProfileId!.Value)
            .Distinct()
            .ToArray();
        var staffNames = await _dbContext.StaffProfiles
            .Where(staff => staffIds.Contains(staff.Id))
            .ToDictionaryAsync(staff => staff.Id, staff => staff.DisplayName, cancellationToken);

        var deliveries = await _dbContext.MembershipGiftDeliveries
            .Where(delivery => delivery.CustomerMembershipId == membership.Id)
            .ToListAsync(cancellationToken);
        var deliveryIds = deliveries.Select(delivery => delivery.Id).ToArray();
        var deliveryReversals = await _dbContext.MembershipGiftDeliveryReversals
            .Where(reversal => deliveryIds.Contains(reversal.MembershipGiftDeliveryId))
            .ToDictionaryAsync(reversal => reversal.MembershipGiftDeliveryId, cancellationToken);
        // A gift is delivered while it has a delivery that was not reversed (at most one, enforced on write).
        var currentDeliveries = deliveries
            .Where(delivery => !deliveryReversals.ContainsKey(delivery.Id))
            .ToDictionary(delivery => delivery.MembershipGiftId);

        var userIds = events.Select(membershipEvent => membershipEvent.PerformedByUserId)
            .Concat(new[] { membership.ActivatedByUserId, membership.AnnulledByUserId }.Where(id => id is not null).Select(id => id!.Value))
            .Concat(consumptions.Select(consumption => consumption.RegisteredByUserId))
            .Concat(reversals.Values.Select(reversal => reversal.PerformedByUserId))
            .Concat(deliveries.Select(delivery => delivery.RegisteredByUserId))
            .Concat(deliveryReversals.Values.Select(reversal => reversal.PerformedByUserId))
            .Distinct()
            .ToArray();
        var userNames = await _dbContext.Users
            .Where(user => userIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.FullName, cancellationToken);

        var customerIds = events.Where(membershipEvent => membershipEvent.RelatedCustomerId is not null)
            .Select(membershipEvent => membershipEvent.RelatedCustomerId!.Value)
            .Append(membership.CustomerId)
            .Append(membership.OriginalCustomerId)
            .Distinct()
            .ToArray();
        var customerNames = await _dbContext.Customers
            .Where(customer => customerIds.Contains(customer.Id))
            .ToDictionaryAsync(customer => customer.Id, customer => customer.FullName, cancellationToken);

        MembershipIncomeView? income = null;
        if (membership.IncomeEntryId is { } incomeEntryId)
        {
            var entry = await _dbContext.IncomeEntries.SingleAsync(candidate => candidate.Id == incomeEntryId, cancellationToken);
            var peers = (await LoadLinkedPeersAsync([incomeEntryId], cancellationToken)).GetValueOrDefault(incomeEntryId) ?? [];
            var total = peers.Sum(peer => peer.Price);
            income = new MembershipIncomeView(entry.Id, entry.Amount, entry.OccurredOn, entry.IsDeleted, total, total != entry.Amount, peers);
        }

        // Consumed = consumption movements minus the reversal movements that gave units back.
        int ConsumedQuantity(Guid allowanceId) => movements
            .Where(movement => movement.MembershipAllowanceId == allowanceId)
            .Sum(movement => movement.Type == MembershipMovementType.Consumption ? movement.Quantity : -movement.Quantity);

        var annulled = membership.Status == MembershipStatus.Annulled;
        var allowances = membership.Allowances
            .OrderBy(allowance => allowance.Position)
            .Select(allowance => new MembershipAllowanceView(
                allowance.Id,
                allowance.Origin.ToString(),
                allowance.MembershipGiftId,
                allowance.GrantedQuantity,
                ConsumedQuantity(allowance.Id),
                allowance.GrantedQuantity - ConsumedQuantity(allowance.Id),
                allowance.Services
                    .OrderBy(service => service.ServiceNameSnapshot, StringComparer.CurrentCultureIgnoreCase)
                    .Select(service => new MembershipAllowanceServiceView(service.ServiceId, service.ServiceNameSnapshot))
                    .ToArray()))
            .ToArray();

        var gifts = membership.Gifts
            .OrderBy(gift => gift.Position)
            .Select(gift =>
            {
                var current = currentDeliveries.GetValueOrDefault(gift.Id);
                // A gift handed over before an annulment stays delivered; a pending one becomes not deliverable.
                var status = gift.Kind == MembershipGiftKind.Service
                    ? (annulled ? "Blocked" : "Granted")
                    : current is not null ? "Delivered" : (annulled ? "NotDeliverable" : "PendingDelivery");
                return new MembershipGiftView(
                    gift.Id,
                    gift.Kind.ToString(),
                    gift.Condition.ToString(),
                    gift.Quantity,
                    gift.ServiceId,
                    gift.ServiceNameSnapshot,
                    gift.DescriptionSnapshot,
                    status,
                    current?.Id,
                    current?.DeliveredOn);
            })
            .ToArray();

        var giftDescriptions = membership.Gifts.ToDictionary(gift => gift.Id, gift => gift.DescriptionSnapshot ?? string.Empty);
        var deliveryViews = deliveries
            .OrderByDescending(delivery => delivery.RegisteredAt)
            .ThenByDescending(delivery => delivery.DeliveryNumber)
            .Select(delivery => new MembershipGiftDeliveryView(
                delivery.Id,
                delivery.MembershipGiftId,
                giftDescriptions.GetValueOrDefault(delivery.MembershipGiftId, string.Empty),
                delivery.Quantity,
                delivery.DeliveredOn,
                delivery.RegisteredAt,
                userNames.GetValueOrDefault(delivery.RegisteredByUserId, string.Empty),
                deliveryReversals.TryGetValue(delivery.Id, out var deliveryReversal)
                    ? new MembershipGiftDeliveryReversalView(
                        deliveryReversal.Reason,
                        deliveryReversal.PerformedAt,
                        userNames.GetValueOrDefault(deliveryReversal.PerformedByUserId, string.Empty))
                    : null))
            .ToArray();

        var eventViews = events.Select(membershipEvent => new MembershipEventView(
                membershipEvent.Id,
                membershipEvent.Type.ToString(),
                membershipEvent.PerformedAt,
                userNames.GetValueOrDefault(membershipEvent.PerformedByUserId, string.Empty),
                membershipEvent.Reason,
                membershipEvent.IncomeEntryId,
                membershipEvent.RelatedCustomerId,
                membershipEvent.RelatedCustomerId is { } relatedId ? customerNames.GetValueOrDefault(relatedId) : null))
            .ToArray();

        var origins = membership.Allowances.ToDictionary(allowance => allowance.Id, allowance => allowance.Origin.ToString());
        var consumptionViews = consumptions
            .OrderByDescending(consumption => consumption.ServiceDate)
            .ThenByDescending(consumption => consumption.RegisteredAt)
            .ThenByDescending(consumption => consumption.Id)
            .Select(consumption => new MembershipConsumptionView(
                consumption.Id,
                consumption.ServiceId,
                consumption.ServiceNameSnapshot,
                consumption.Quantity,
                consumption.ServiceDate,
                consumption.StaffProfileId,
                consumption.StaffProfileId is { } staffId ? staffNames.GetValueOrDefault(staffId) : null,
                consumption.RegisteredAt,
                userNames.GetValueOrDefault(consumption.RegisteredByUserId, string.Empty),
                movements
                    .Where(movement => movement.MembershipConsumptionId == consumption.Id && movement.Type == MembershipMovementType.Consumption)
                    .OrderBy(movement => origins[movement.MembershipAllowanceId] == nameof(MembershipAllowanceOrigin.Gift) ? 0 : 1)
                    .Select(movement => new MembershipConsumptionPartView(movement.MembershipAllowanceId, origins[movement.MembershipAllowanceId], movement.Quantity))
                    .ToArray(),
                reversals.TryGetValue(consumption.Id, out var reversal)
                    ? new MembershipConsumptionReversalView(reversal.Reason, reversal.PerformedAt, userNames.GetValueOrDefault(reversal.PerformedByUserId, string.Empty))
                    : null))
            .ToArray();

        return new CustomerMembershipDetailView(
            membership.Id,
            membership.CustomerId,
            customerNames.GetValueOrDefault(membership.CustomerId, string.Empty),
            membership.OriginalCustomerId,
            customerNames.GetValueOrDefault(membership.OriginalCustomerId, string.Empty),
            membership.MembershipPlanId,
            membership.PlanName,
            membership.PlanDescription,
            membership.Price,
            membership.Mode.ToString(),
            membership.Terms,
            membership.SpecificConditions,
            membership.SoldOn,
            membership.StartsOn,
            membership.EndsOn,
            membership.Status.ToString(),
            DisplayStatus(membership, Today()),
            membership.ActivatedAt,
            membership.ActivatedByUserId is { } activatedBy ? userNames.GetValueOrDefault(activatedBy) : null,
            membership.AnnulledAt,
            membership.AnnulledByUserId is { } annulledBy ? userNames.GetValueOrDefault(annulledBy) : null,
            membership.AnnulmentReason,
            allowances,
            gifts,
            income,
            eventViews,
            consumptionViews,
            deliveryViews,
            membership.CreatedAt);
    }
}
