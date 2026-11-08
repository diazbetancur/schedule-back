using System.Security.Cryptography;
using System.Text;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Common.Paging;
using Barbershop.Application.Loyalty.Wallet;
using Barbershop.Domain.Common;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.Memberships;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Barbershop.Infrastructure.Loyalty;

/// <summary>
/// The points wallet (ADR-0013, ADR-0014). Movements are append-only history; the remaining points of a lot and the
/// outstanding points of a debt are projections updated in the same transaction as the movement that changes them.
/// Every change to a wallet first takes the lock of its customer profile. Global lock order (ADR-0014, ADR-0015):
/// service record → income → customer profiles (ascending id) → redemptions → memberships; lots, debts, reservations
/// and redemptions of a profile are only changed under that profile's lock.
/// </summary>
internal sealed class LoyaltyWalletService : IMyLoyaltyService, IAdminCustomerLoyaltyService
{
    /// <summary>Longest chain of merged profiles followed before giving up with an explicit error.</summary>
    internal const int MaxMergeChainDepth = 32;

    internal const int ExpiringSoonDays = 30;
    /// <summary>Redemptions embedded in the admin wallet (a per-customer view, not a paginated history).</summary>
    /// <summary>Closed redemptions shown in the wallet summary; the full list is the paginated redemptions page.</summary>
    internal const int RecentRedemptionsPreviewSize = 10;

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public LoyaltyWalletService(AppDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    // ── Merge chains (ADR-0013 D6) ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Follows <c>MergedIntoCustomerId</c> to the live profile without rewriting anything. A cycle or a chain longer
    /// than <see cref="MaxMergeChainDepth"/> is an explicit error, never a partial answer.
    /// </summary>
    internal async Task<Guid> ResolveLiveCustomerIdAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var visited = new HashSet<Guid>();
        var current = customerId;
        while (true)
        {
            if (!visited.Add(current))
            {
                throw new ConflictException($"La cadena de fusiones del cliente {customerId} tiene un ciclo; no se puede determinar el perfil vigente.");
            }

            if (visited.Count > MaxMergeChainDepth)
            {
                throw new ConflictException(
                    $"La cadena de fusiones del cliente {customerId} supera {MaxMergeChainDepth} niveles; no se puede determinar el perfil vigente.");
            }

            var row = await _dbContext.Customers.AsNoTracking()
                .Where(customer => customer.Id == current)
                .Select(customer => new { customer.MergedIntoCustomerId })
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new NotFoundException("El cliente no existe.");
            if (row.MergedIntoCustomerId is not { } next)
            {
                return current;
            }

            current = next;
        }
    }

    // ── Locks ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Row locks on customer profiles, always in ascending id order; a no-op outside PostgreSQL.</summary>
    internal async Task LockCustomersAsync(IEnumerable<Guid> customerIds, CancellationToken cancellationToken)
    {
        if (!_dbContext.Database.IsNpgsql())
        {
            return;
        }

        foreach (var customerId in customerIds.Distinct().Order())
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM public.customers WHERE \"Id\" = {customerId} FOR UPDATE", cancellationToken);
        }
    }

    /// <summary>Locks the live profile behind <paramref name="customerId"/>; re-resolves if it was merged meanwhile.</summary>
    internal async Task<Guid> LockLiveCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxMergeChainDepth; attempt++)
        {
            var live = await ResolveLiveCustomerIdAsync(customerId, cancellationToken);
            await LockCustomersAsync([live], cancellationToken);
            var stillLive = await _dbContext.Customers.AsNoTracking()
                .AnyAsync(customer => customer.Id == live && customer.MergedIntoCustomerId == null, cancellationToken);
            if (stillLive)
            {
                return live;
            }

            customerId = live;
        }

        throw new ConflictException("El cliente cambió mientras se registraban sus puntos. Intenta de nuevo.");
    }

    // ── Changes inside the caller's transaction ────────────────────────────────────────────────

    /// <summary>
    /// Opens the lot of an original accrual, once per source (a retry returns null and adds nothing), with the
    /// validity of the settings right now, then applies the common debt settlement rule. The caller saves and commits.
    /// </summary>
    internal async Task<LoyaltyLot?> AccrueInCurrentTransactionAsync(
        Guid customerId,
        LoyaltySourceType sourceType,
        Guid sourceId,
        int points,
        Guid performedByUserId,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        EnsureTransaction();
        var live = await LockLiveCustomerAsync(customerId, cancellationToken);

        var alreadyAccrued = _dbContext.LoyaltyLots.Local.Any(lot => IsOriginalOf(lot, sourceType, sourceId))
            || await _dbContext.LoyaltyLots.AnyAsync(
                lot => lot.SourceType == sourceType && lot.SourceId == sourceId && lot.OriginLotId == null, cancellationToken);
        if (alreadyAccrued)
        {
            return null;
        }

        var validityDays = await ValidityDaysAsync(cancellationToken);
        var lot = LoyaltyLot.Accrue(live, sourceType, sourceId, points, utcNow, BogotaDate(utcNow), validityDays);
        _dbContext.LoyaltyLots.Add(lot);
        _dbContext.LoyaltyMovements.Add(new LoyaltyMovement(
            live, LoyaltyMovementType.Accrual, points, utcNow, lotId: lot.Id, performedByUserId: performedByUserId));

        await SettleDebtsAsync(live, performedByUserId, utcNow, cancellationToken);
        return lot;
    }

    /// <summary>
    /// Takes the still usable points of the lots a membership earned, wherever merges moved them, from the profile the
    /// caller locked. Expired points and points of other sources are untouched, and no debt is created. A lot can be
    /// withdrawn only once (unique index), so a retry never repeats it.
    /// </summary>
    internal async Task<int> WithdrawForMembershipAnnulmentInCurrentTransactionAsync(
        Guid membershipId, Guid lockedCustomerId, string reason, Guid performedByUserId, DateTime utcNow, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        var today = BogotaDate(utcNow);
        var lots = await _dbContext.LoyaltyLots
            .Where(lot => lot.SourceType == LoyaltySourceType.Membership && lot.SourceId == membershipId && lot.RemainingPoints > 0)
            .ToListAsync(cancellationToken);

        var withdrawn = 0;
        foreach (var lot in lots.Where(candidate => !candidate.IsExpiredOn(today)))
        {
            if (lot.CustomerId != lockedCustomerId)
            {
                // Merges move a membership and its lots together; anything else means the data changed under us.
                throw new ConflictException("Los puntos de la membresía están en otro perfil. Recarga e intenta de nuevo.");
            }

            var points = lot.RemainingPoints;
            lot.Take(points);
            _dbContext.LoyaltyMovements.Add(new LoyaltyMovement(
                lot.CustomerId, LoyaltyMovementType.MembershipAnnulmentWithdrawal, points, utcNow,
                lotId: lot.Id, performedByUserId: performedByUserId, reason: reason));
            withdrawn += points;
        }

        return withdrawn;
    }

    /// <summary>
    /// Moves the usable points and the outstanding debt of <paramref name="sourceId"/> to <paramref name="targetId"/>
    /// with new movements (ADR-0013 D1–D4): each new lot keeps the original expiry and points to its source lot and
    /// profile; expired points stay behind; debts move by their outstanding part only, then the common settlement
    /// rule runs on the target. The caller holds both locks and saves.
    /// </summary>
    internal async Task TransferForMergeInCurrentTransactionAsync(
        Guid sourceId, Guid targetId, Guid performedByUserId, DateTime utcNow, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        var today = BogotaDate(utcNow);

        var sourceLots = await _dbContext.LoyaltyLots
            .Where(lot => lot.CustomerId == sourceId && lot.RemainingPoints > 0)
            .OrderBy(lot => lot.ExpiresOn).ThenBy(lot => lot.AccruedAt)
            .ToListAsync(cancellationToken);
        foreach (var lot in sourceLots.Where(candidate => !candidate.IsExpiredOn(today)))
        {
            var points = lot.RemainingPoints;
            lot.Take(points);
            _dbContext.LoyaltyMovements.Add(new LoyaltyMovement(
                sourceId, LoyaltyMovementType.MergeTransferOut, points, utcNow,
                lotId: lot.Id, performedByUserId: performedByUserId, relatedCustomerId: targetId));

            var moved = LoyaltyLot.TransferredFrom(lot, targetId, points, utcNow);
            _dbContext.LoyaltyLots.Add(moved);
            _dbContext.LoyaltyMovements.Add(new LoyaltyMovement(
                targetId, LoyaltyMovementType.MergeTransferIn, points, utcNow,
                lotId: moved.Id, performedByUserId: performedByUserId, relatedCustomerId: sourceId));
        }

        var sourceDebts = await _dbContext.LoyaltyDebts
            .Where(debt => debt.CustomerId == sourceId && debt.OutstandingPoints > 0)
            .OrderBy(debt => debt.IncurredAt).ThenBy(debt => debt.Id)
            .ToListAsync(cancellationToken);
        foreach (var debt in sourceDebts)
        {
            var points = debt.OutstandingPoints;
            debt.Reduce(points);
            _dbContext.LoyaltyMovements.Add(new LoyaltyMovement(
                sourceId, LoyaltyMovementType.DebtMergeTransferOut, points, utcNow,
                debtId: debt.Id, performedByUserId: performedByUserId, relatedCustomerId: targetId));

            // Keeps the original IncurredAt: the merge must not change which debt is settled first.
            var moved = LoyaltyDebt.TransferredFrom(debt, targetId, points, utcNow);
            _dbContext.LoyaltyDebts.Add(moved);
            _dbContext.LoyaltyMovements.Add(new LoyaltyMovement(
                targetId, LoyaltyMovementType.DebtMergeTransferIn, points, utcNow,
                debtId: moved.Id, performedByUserId: performedByUserId, relatedCustomerId: sourceId));
        }

        await SettleDebtsAsync(targetId, performedByUserId, utcNow, cancellationToken);
    }

    /// <summary>
    /// The common rule (ADR-0013 D4): outstanding debts, oldest first by <see cref="LoyaltyDebt.IncurredAt"/>, are settled with usable points, soonest expiry
    /// first. Each settlement is one movement against one debt and one lot, and neither can go below zero.
    /// </summary>
    internal async Task SettleDebtsAsync(Guid customerId, Guid? performedByUserId, DateTime utcNow, CancellationToken cancellationToken)
    {
        var debts = (await TrackedAsync(_dbContext.LoyaltyDebts, debt => debt.CustomerId == customerId && debt.OutstandingPoints > 0, cancellationToken))
            .Where(debt => debt.OutstandingPoints > 0)
            .OrderBy(debt => debt.IncurredAt).ThenBy(debt => debt.Id)
            .ToList();
        if (debts.Count == 0)
        {
            return;
        }

        var today = BogotaDate(utcNow);
        var lots = (await TrackedAsync(_dbContext.LoyaltyLots, lot => lot.CustomerId == customerId && lot.RemainingPoints > 0, cancellationToken))
            .Where(lot => lot.UsablePointsOn(today) > 0)
            .OrderBy(lot => lot.ExpiresOn).ThenBy(lot => lot.AccruedAt).ThenBy(lot => lot.Id)
            .ToList();

        foreach (var debt in debts)
        {
            foreach (var lot in lots.Where(candidate => candidate.RemainingPoints > 0))
            {
                if (debt.OutstandingPoints == 0)
                {
                    break;
                }

                var points = Math.Min(debt.OutstandingPoints, lot.RemainingPoints);
                lot.Take(points);
                debt.Reduce(points);
                _dbContext.LoyaltyMovements.Add(new LoyaltyMovement(
                    customerId, LoyaltyMovementType.DebtSettlement, points, utcNow,
                    lotId: lot.Id, debtId: debt.Id, performedByUserId: performedByUserId));
            }
        }
    }

    // ── Redemption reservations (ADR-0015) ─────────────────────────────────────────────────────

    /// <summary>Usable free points minus outstanding debt (R9), including changes not saved yet.</summary>
    internal async Task<int> AvailablePointsAsync(Guid customerId, DateTime utcNow, CancellationToken cancellationToken)
    {
        var usable = (await TrackedUsableLotsAsync(customerId, utcNow, cancellationToken)).Sum(lot => lot.RemainingPoints);
        var owed = (await TrackedAsync(_dbContext.LoyaltyDebts, debt => debt.CustomerId == customerId && debt.OutstandingPoints > 0, cancellationToken))
            .Sum(debt => debt.OutstandingPoints);
        return usable - owed;
    }

    /// <summary>
    /// Reserves <paramref name="points"/> for a redemption from the usable lots of the profile the caller locked,
    /// soonest expiry first, recording how many came from each lot. The available balance (usable minus debt) must
    /// cover them; reserved points leave the free balance, so the expiration job never takes them.
    /// </summary>
    internal async Task ReserveInCurrentTransactionAsync(
        Guid lockedCustomerId, Guid redemptionId, int points, Guid performedByUserId, DateTime utcNow, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        var available = await AvailablePointsAsync(lockedCustomerId, utcNow, cancellationToken);
        if (points > available)
        {
            throw new ConflictException(
                $"No hay puntos suficientes: la recompensa cuesta {points} y hay {Math.Max(0, available)} disponible(s).");
        }

        var pending = points;
        foreach (var lot in await TrackedUsableLotsAsync(lockedCustomerId, utcNow, cancellationToken))
        {
            if (pending == 0)
            {
                break;
            }

            var taken = Math.Min(pending, lot.RemainingPoints);
            lot.Reserve(taken);
            pending -= taken;
            _dbContext.LoyaltyRedemptionAllocations.Add(new LoyaltyRedemptionAllocation(redemptionId, lot.Id, taken));
            _dbContext.LoyaltyMovements.Add(new LoyaltyMovement(
                lockedCustomerId, LoyaltyMovementType.RedemptionHold, taken, utcNow,
                lotId: lot.Id, performedByUserId: performedByUserId, redemptionId: redemptionId));
        }
    }

    /// <summary>
    /// Gives back exactly what the redemption reserved, to the same lots. Points released into a lot already past its
    /// expiry expire in this same transaction (even if the lot's free balance expired before), and points of a lot
    /// whose membership was annulled meanwhile are withdrawn: nothing comes back usable that should not. Usable
    /// points released then settle debts with the common rule. The caller holds the profile lock and makes this
    /// happen once (the redemption's transition, and a unique index per lot, redemption and movement type).
    /// </summary>
    internal async Task ReleaseReservationInCurrentTransactionAsync(
        Guid lockedCustomerId, Guid redemptionId, Guid? performedByUserId, DateTime utcNow, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        var today = BogotaDate(utcNow);
        var (allocations, lots) = await LoadReservationAsync(lockedCustomerId, redemptionId, cancellationToken);

        var membershipIds = lots.Values.Where(lot => lot.SourceType == LoyaltySourceType.Membership).Select(lot => lot.SourceId).Distinct().ToArray();
        var annulled = membershipIds.Length == 0
            ? new HashSet<Guid>()
            : (await _dbContext.CustomerMemberships.AsNoTracking()
                .Where(membership => membershipIds.Contains(membership.Id) && membership.Status == MembershipStatus.Annulled)
                .Select(membership => membership.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        foreach (var allocation in allocations)
        {
            var lot = lots[allocation.LotId];
            lot.Release(allocation.Points);
            _dbContext.LoyaltyMovements.Add(new LoyaltyMovement(
                lockedCustomerId, LoyaltyMovementType.RedemptionRelease, allocation.Points, utcNow,
                lotId: lot.Id, performedByUserId: performedByUserId, redemptionId: redemptionId));

            if (lot.IsExpiredOn(today))
            {
                lot.Take(allocation.Points);
                _dbContext.LoyaltyMovements.Add(new LoyaltyMovement(
                    lockedCustomerId, LoyaltyMovementType.Expiration, allocation.Points, utcNow, lotId: lot.Id, redemptionId: redemptionId));
            }
            else if (lot.SourceType == LoyaltySourceType.Membership && annulled.Contains(lot.SourceId))
            {
                lot.Take(allocation.Points);
                _dbContext.LoyaltyMovements.Add(new LoyaltyMovement(
                    lockedCustomerId, LoyaltyMovementType.MembershipAnnulmentWithdrawal, allocation.Points, utcNow,
                    lotId: lot.Id, performedByUserId: performedByUserId, reason: "Membresía anulada mientras los puntos estaban reservados.",
                    redemptionId: redemptionId));
            }
        }

        await SettleDebtsAsync(lockedCustomerId, performedByUserId, utcNow, cancellationToken);
    }

    /// <summary>A delivery consumes the reservation lot by lot; the free balance is not debited again.</summary>
    internal async Task ConsumeReservationInCurrentTransactionAsync(
        Guid lockedCustomerId, Guid redemptionId, Guid performedByUserId, DateTime utcNow, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        var (allocations, lots) = await LoadReservationAsync(lockedCustomerId, redemptionId, cancellationToken);
        foreach (var allocation in allocations)
        {
            lots[allocation.LotId].ConsumeReserved(allocation.Points);
            _dbContext.LoyaltyMovements.Add(new LoyaltyMovement(
                lockedCustomerId, LoyaltyMovementType.RedemptionConsumed, allocation.Points, utcNow,
                lotId: allocation.LotId, performedByUserId: performedByUserId, redemptionId: redemptionId));
        }
    }

    private async Task<(IReadOnlyList<LoyaltyRedemptionAllocation> Allocations, Dictionary<Guid, LoyaltyLot> Lots)> LoadReservationAsync(
        Guid lockedCustomerId, Guid redemptionId, CancellationToken cancellationToken)
    {
        var allocations = await TrackedAsync(
            _dbContext.LoyaltyRedemptionAllocations, allocation => allocation.RedemptionId == redemptionId, cancellationToken);
        var lotIds = allocations.Select(allocation => allocation.LotId).ToArray();
        var lots = (await TrackedAsync(_dbContext.LoyaltyLots, lot => lotIds.Contains(lot.Id), cancellationToken))
            .ToDictionary(lot => lot.Id);
        if (lots.Values.Any(lot => lot.CustomerId != lockedCustomerId))
        {
            // Pending redemptions block merges, so their lots never move; anything else means the data changed under us.
            throw new ConflictException("Los puntos reservados están en otro perfil. Recarga e intenta de nuevo.");
        }

        return (allocations.OrderBy(allocation => allocation.LotId).ToList(), lots);
    }

    // ── Expiration (daily job) ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records the expiration of lots whose last valid day has passed, one profile per transaction. Idempotent: an
    /// expired lot has no points left, and the unique index allows one expiration per lot.
    /// </summary>
    internal async Task<int> ExpireDueLotsAsync(CancellationToken cancellationToken)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var today = BogotaDate(utcNow);
        var customerIds = await _dbContext.LoyaltyLots.AsNoTracking()
            .Where(lot => lot.RemainingPoints > 0 && lot.ExpiresOn < today)
            .Select(lot => lot.CustomerId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var expired = 0;
        foreach (var customerId in customerIds)
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            await LockCustomersAsync([customerId], cancellationToken);
            var lots = await _dbContext.LoyaltyLots
                .Where(lot => lot.CustomerId == customerId && lot.RemainingPoints > 0 && lot.ExpiresOn < today)
                .ToListAsync(cancellationToken);
            foreach (var lot in lots)
            {
                var points = lot.RemainingPoints;
                lot.Take(points);
                _dbContext.LoyaltyMovements.Add(new LoyaltyMovement(customerId, LoyaltyMovementType.Expiration, points, utcNow, lotId: lot.Id));
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _dbContext.ChangeTracker.Clear();
            expired += lots.Count;
        }

        return expired;
    }

    // ── Merge preview (ADR-0013 D7) ────────────────────────────────────────────────────────────

    /// <summary>
    /// The points side of a merge preview and a fingerprint of everything it depends on (lots, debts, their
    /// versions and the Bogotá date, since a lot can expire overnight).
    /// </summary>
    internal async Task<(MergeLoyaltyPreview Preview, string Fingerprint)> BuildMergePreviewAsync(
        Guid sourceId, Guid targetId, CancellationToken cancellationToken)
    {
        var today = Today();
        var lots = await _dbContext.LoyaltyLots.AsNoTracking()
            .Where(lot => (lot.CustomerId == sourceId || lot.CustomerId == targetId) && lot.RemainingPoints > 0)
            .ToListAsync(cancellationToken);
        var debts = await _dbContext.LoyaltyDebts.AsNoTracking()
            .Where(debt => (debt.CustomerId == sourceId || debt.CustomerId == targetId) && debt.OutstandingPoints > 0)
            .ToListAsync(cancellationToken);

        int Usable(Guid customerId) => lots.Where(lot => lot.CustomerId == customerId).Sum(lot => lot.UsablePointsOn(today));
        int Owed(Guid customerId) => debts.Where(debt => debt.CustomerId == customerId).Sum(debt => debt.OutstandingPoints);

        var available = Usable(sourceId) + Usable(targetId);
        var owed = Owed(sourceId) + Owed(targetId);
        var toTransfer = lots
            .Where(lot => lot.CustomerId == sourceId && !lot.IsExpiredOn(today))
            .OrderBy(lot => lot.ExpiresOn).ThenBy(lot => lot.AccruedAt)
            .ToList();
        var labels = await SourceLabelsAsync(toTransfer, cancellationToken);

        var preview = new MergeLoyaltyPreview(
            Usable(sourceId),
            Owed(sourceId),
            Usable(targetId),
            Owed(targetId),
            Math.Max(0, available - owed),
            Math.Max(0, owed - available),
            lots.Where(lot => lot.CustomerId == sourceId && lot.IsExpiredOn(today)).Sum(lot => lot.RemainingPoints),
            toTransfer.Select(lot => new MergeLoyaltyLotPreview(
                    lot.Id, labels[lot.Id], lot.RemainingPoints, lot.ExpiresOn, lot.ExpiresOn <= today.AddDays(ExpiringSoonDays)))
                .ToArray());

        var fingerprint = new StringBuilder().Append(today.ToString("O"));
        foreach (var lot in lots.OrderBy(lot => lot.Id))
        {
            fingerprint.Append($"|L{lot.Id}:{lot.CustomerId}:{lot.RemainingPoints}:{lot.ExpiresOn:O}:{lot.Version}");
        }

        foreach (var debt in debts.OrderBy(debt => debt.Id))
        {
            fingerprint.Append($"|D{debt.Id}:{debt.CustomerId}:{debt.OutstandingPoints}:{debt.Version}");
        }

        return (preview, fingerprint.ToString());
    }

    internal static string HashToken(string fingerprint)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)));

    // ── Queries ────────────────────────────────────────────────────────────────────────────────

    public async Task<LoyaltyWalletView> GetWalletAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        var live = await ResolveLiveCustomerIdAsync(customerId, cancellationToken);
        var today = Today();

        var lots = await _dbContext.LoyaltyLots.AsNoTracking()
            .Where(lot => lot.CustomerId == customerId)
            .OrderBy(lot => lot.ExpiresOn).ThenBy(lot => lot.AccruedAt)
            .ToListAsync(cancellationToken);
        var debts = await _dbContext.LoyaltyDebts.AsNoTracking()
            .Where(debt => debt.CustomerId == customerId)
            .OrderBy(debt => debt.IncurredAt).ThenBy(debt => debt.Id)
            .ToListAsync(cancellationToken);
        var movements = await _dbContext.LoyaltyMovements.AsNoTracking()
            .Where(movement => movement.CustomerId == customerId)
            .OrderByDescending(movement => movement.PerformedAt).ThenBy(movement => movement.Type).ThenBy(movement => movement.Id)
            .Take(LoyaltyMovementListQuery.RecentPreviewSize)
            .ToListAsync(cancellationToken);

        var movementViews = await MapMovementsAsync(movements, cancellationToken);
        var labels = await SourceLabelsAsync(lots, cancellationToken);
        var usableLots = lots.Where(lot => lot.UsablePointsOn(today) > 0).ToList();

        return new LoyaltyWalletView(
            customerId,
            live,
            live != customerId,
            await IsProgramEnabledAsync(cancellationToken),
            usableLots.Sum(lot => lot.RemainingPoints),
            debts.Sum(debt => debt.OutstandingPoints),
            usableLots.Where(lot => lot.ExpiresOn <= today.AddDays(ExpiringSoonDays)).Sum(lot => lot.RemainingPoints),
            usableLots.Count == 0 ? null : usableLots.Min(lot => lot.ExpiresOn),
            lots.Select(lot => new LoyaltyLotView(
                    lot.Id, lot.SourceType.ToString(), lot.SourceId, labels[lot.Id], lot.InitialPoints, lot.RemainingPoints,
                    lot.UsablePointsOn(today), lot.AccruedOn, lot.ValidityDays, lot.ExpiresOn, lot.IsExpiredOn(today),
                    lot.OriginLotId, lot.OriginCustomerId, lot.ReservedPoints))
                .ToArray(),
            debts.Select(debt => new LoyaltyDebtView(
                    debt.Id, debt.Origin.ToString(), debt.Amount, debt.OutstandingPoints, debt.OriginDebtId, debt.OriginCustomerId, debt.CreatedAt, debt.IncurredAt))
                .ToArray(),
            movementViews,
            lots.Sum(lot => lot.ReservedPoints),
            await LoyaltyRedemptionService.MapAsync(
                _dbContext,
                await RedemptionsSummaryAsync(customerId, cancellationToken),
                today,
                cancellationToken));
    }

    /// <summary>
    /// Summary of the wallet: every pending redemption (requested or approved, however old, because they still need
    /// work) plus the <see cref="RecentRedemptionsPreviewSize"/> most recent closed ones, newest first. The complete
    /// list is the paginated redemptions page filtered by customer.
    /// </summary>
    private async Task<List<LoyaltyRedemption>> RedemptionsSummaryAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var ofCustomer = _dbContext.LoyaltyRedemptions.AsNoTracking().Where(redemption => redemption.CustomerId == customerId);
        var pending = await ofCustomer
            .Where(redemption => redemption.Status == LoyaltyRedemptionStatus.Requested || redemption.Status == LoyaltyRedemptionStatus.Approved)
            .ToListAsync(cancellationToken);
        var recentClosed = await ofCustomer
            .Where(redemption => redemption.Status != LoyaltyRedemptionStatus.Requested && redemption.Status != LoyaltyRedemptionStatus.Approved)
            .OrderByDescending(redemption => redemption.RequestedAt).ThenByDescending(redemption => redemption.Id)
            .Take(RecentRedemptionsPreviewSize)
            .ToListAsync(cancellationToken);
        return pending.Concat(recentClosed)
            .OrderByDescending(redemption => redemption.RequestedAt).ThenByDescending(redemption => redemption.Id)
            .ToList();
    }

    public async Task<PagedResult<LoyaltyMovementView>> GetMovementsAsync(
        Guid customerId, LoyaltyMovementListQuery query, CancellationToken cancellationToken = default)
    {
        var (page, movements) = ParseMovementQuery(query);
        if (!await _dbContext.Customers.AnyAsync(customer => customer.Id == customerId, cancellationToken))
        {
            throw new NotFoundException("El cliente no existe.");
        }

        return await movements(customerId).ToPagedResultAsync(page, MapMovementsAsync, cancellationToken);
    }

    public async Task<PagedResult<MyLoyaltyMovementView>> GetMyMovementsAsync(
        Guid currentUserId, LoyaltyMovementListQuery query, CancellationToken cancellationToken = default)
    {
        var (page, movements) = ParseMovementQuery(query);
        var profileId = await _dbContext.Customers.AsNoTracking()
            .Where(customer => customer.UserId == currentUserId && customer.MergedIntoCustomerId == null)
            .Select(customer => (Guid?)customer.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (profileId is not { } customerId)
        {
            return new PagedResult<MyLoyaltyMovementView>([], page.Page, page.PageSize, 0);
        }

        return await movements(customerId).ToPagedResultAsync(
            page, movement => new MyLoyaltyMovementView(movement.Type.ToString(), movement.Points, movement.PerformedAt), cancellationToken);
    }

    /// <summary>Validates the filters and returns the filtered, totally ordered movements of one profile (newest first).</summary>
    private (PageRequest Page, Func<Guid, IQueryable<LoyaltyMovement>> Movements) ParseMovementQuery(LoyaltyMovementListQuery query)
    {
        var page = PageRequest.Create(query.Page, query.PageSize);
        var type = ListFilters.ParseEnum<LoyaltyMovementType>(query.Type, "type");
        // A history: the last 30 days by default and at most 30 days per query (any period). Balances never depend on it.
        var range = HistoryRange.Resolve(query.From, query.To, Today());
        DateTime? fromUtc = PagingExtensions.StartOfBogotaDayUtc(range.From);
        DateTime? toUtc = PagingExtensions.EndOfBogotaDayUtc(range.To);

        return (page, customerId =>
        {
            var movements = _dbContext.LoyaltyMovements.AsNoTracking().Where(movement => movement.CustomerId == customerId);
            if (type is { } wanted)
            {
                movements = movements.Where(movement => movement.Type == wanted);
            }

            if (fromUtc is { } start)
            {
                movements = movements.Where(movement => movement.PerformedAt >= start);
            }

            if (toUtc is { } end)
            {
                movements = movements.Where(movement => movement.PerformedAt < end);
            }

            return movements
                .OrderByDescending(movement => movement.PerformedAt)
                .ThenBy(movement => movement.Type)
                .ThenBy(movement => movement.Id);
        });
    }

    private async Task<IReadOnlyList<LoyaltyMovementView>> MapMovementsAsync(
        IReadOnlyList<LoyaltyMovement> movements, CancellationToken cancellationToken)
    {
        var userIds = movements.Where(movement => movement.PerformedByUserId is not null).Select(movement => movement.PerformedByUserId!.Value).Distinct().ToArray();
        var userNames = await _dbContext.Users.AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.FullName, cancellationToken);
        return movements.Select(movement => new LoyaltyMovementView(
                movement.Id, movement.Type.ToString(), movement.Points, movement.LotId, movement.DebtId, movement.Reason,
                movement.RelatedCustomerId,
                movement.PerformedByUserId is { } userId ? userNames.GetValueOrDefault(userId) : null,
                movement.PerformedAt,
                movement.RedemptionId))
            .ToArray();
    }

    public async Task<MyLoyaltyView> GetMineAsync(Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var programEnabled = await IsProgramEnabledAsync(cancellationToken);
        var profileId = await _dbContext.Customers.AsNoTracking()
            .Where(customer => customer.UserId == currentUserId && customer.MergedIntoCustomerId == null)
            .Select(customer => (Guid?)customer.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (profileId is not { } customerId)
        {
            return new MyLoyaltyView(false, programEnabled, 0, 0, 0, null, [], []);
        }

        var today = Today();
        var reserved = await _dbContext.LoyaltyLots.AsNoTracking()
            .Where(lot => lot.CustomerId == customerId)
            .SumAsync(lot => lot.ReservedPoints, cancellationToken);
        var lots = (await _dbContext.LoyaltyLots.AsNoTracking()
                .Where(lot => lot.CustomerId == customerId && lot.RemainingPoints > 0)
                .ToListAsync(cancellationToken))
            .Where(lot => !lot.IsExpiredOn(today))
            .OrderBy(lot => lot.ExpiresOn).ThenBy(lot => lot.AccruedAt)
            .ToList();
        var debt = await _dbContext.LoyaltyDebts.AsNoTracking()
            .Where(candidate => candidate.CustomerId == customerId)
            .SumAsync(candidate => candidate.OutstandingPoints, cancellationToken);
        var movements = await _dbContext.LoyaltyMovements.AsNoTracking()
            .Where(movement => movement.CustomerId == customerId)
            .OrderByDescending(movement => movement.PerformedAt).ThenBy(movement => movement.Type).ThenBy(movement => movement.Id)
            .Take(LoyaltyMovementListQuery.RecentPreviewSize)
            .Select(movement => new { movement.Type, movement.Points, movement.PerformedAt })
            .ToListAsync(cancellationToken);
        var labels = await SourceLabelsAsync(lots, cancellationToken);

        return new MyLoyaltyView(
            true,
            programEnabled,
            lots.Sum(lot => lot.RemainingPoints),
            debt,
            lots.Where(lot => lot.ExpiresOn <= today.AddDays(ExpiringSoonDays)).Sum(lot => lot.RemainingPoints),
            lots.Count == 0 ? null : lots[0].ExpiresOn,
            lots.Select(lot => new MyLoyaltyLotView(lot.RemainingPoints, lot.AccruedOn, lot.ExpiresOn, labels[lot.Id])).ToArray(),
            movements.Select(movement => new MyLoyaltyMovementView(movement.Type.ToString(), movement.Points, movement.PerformedAt)).ToArray(),
            reserved);
    }

    // ── Manual adjustments ─────────────────────────────────────────────────────────────────────

    public async Task<LoyaltyWalletView> AdjustAsync(
        Guid currentUserId, Guid customerId, LoyaltyAdjustmentRequest request, CancellationToken cancellationToken = default)
    {
        var direction = ValidateAdjustment(request, out var reason);

        if (await ReplayAdjustmentAsync(customerId, direction, request, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        if (!await _dbContext.Customers.AnyAsync(customer => customer.Id == customerId, cancellationToken))
        {
            throw new NotFoundException("El cliente no existe.");
        }

        try
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            await LockCustomersAsync([customerId], cancellationToken);
            if (!await _dbContext.Customers.AsNoTracking().AnyAsync(customer => customer.Id == customerId && customer.MergedIntoCustomerId == null, cancellationToken))
            {
                throw new ConflictException("El cliente fue fusionado en otro perfil; ajusta los puntos en el perfil que se conserva.");
            }

            // The same key stored while this request waited for the lock.
            if (await ReplayAdjustmentAsync(customerId, direction, request, cancellationToken) is { } concurrent)
            {
                return concurrent;
            }

            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
            var adjustment = new LoyaltyAdjustment(customerId, direction, request.Points, reason, request.IdempotencyKey, currentUserId, utcNow);
            _dbContext.LoyaltyAdjustments.Add(adjustment);

            if (direction == LoyaltyAdjustmentDirection.Credit)
            {
                // Allowed with the program disabled: it corrects balances, it is not an accrual.
                var lot = LoyaltyLot.Accrue(
                    customerId, LoyaltySourceType.Adjustment, adjustment.Id, request.Points, utcNow, BogotaDate(utcNow),
                    await ValidityDaysAsync(cancellationToken));
                _dbContext.LoyaltyLots.Add(lot);
                _dbContext.LoyaltyMovements.Add(new LoyaltyMovement(
                    customerId, LoyaltyMovementType.AdjustmentCredit, request.Points, utcNow,
                    lotId: lot.Id, performedByUserId: currentUserId, adjustmentId: adjustment.Id, reason: reason));
                await SettleDebtsAsync(customerId, currentUserId, utcNow, cancellationToken);
            }
            else
            {
                DebitUsablePoints(customerId, adjustment, await TrackedUsableLotsAsync(customerId, utcNow, cancellationToken), utcNow);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The same key won a race: return it (the transaction is rolled back here).
            _dbContext.ChangeTracker.Clear();
            return await ReplayAdjustmentAsync(customerId, direction, request, cancellationToken)
                ?? throw new ConflictException("El ajuste ya se registró. Recarga e intenta de nuevo.");
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("Los puntos del cliente cambiaron mientras se registraba el ajuste. Recarga e intenta de nuevo.");
        }

        _dbContext.ChangeTracker.Clear();
        return await GetWalletAsync(customerId, cancellationToken);
    }

    /// <summary>A debit never goes beyond the usable points (no new debt in stage 4), soonest expiry first.</summary>
    private void DebitUsablePoints(Guid customerId, LoyaltyAdjustment adjustment, IReadOnlyList<LoyaltyLot> usableLots, DateTime utcNow)
    {
        var available = usableLots.Sum(lot => lot.RemainingPoints);
        if (adjustment.Points > available)
        {
            throw new ConflictException($"Solo puedes descontar hasta {available} punto(s) disponibles; un cargo nunca genera deuda.");
        }

        var pending = adjustment.Points;
        foreach (var lot in usableLots)
        {
            if (pending == 0)
            {
                break;
            }

            var points = Math.Min(pending, lot.RemainingPoints);
            lot.Take(points);
            pending -= points;
            _dbContext.LoyaltyMovements.Add(new LoyaltyMovement(
                customerId, LoyaltyMovementType.AdjustmentDebit, points, utcNow,
                lotId: lot.Id, performedByUserId: adjustment.PerformedByUserId, adjustmentId: adjustment.Id, reason: adjustment.Reason));
        }
    }

    private async Task<LoyaltyWalletView?> ReplayAdjustmentAsync(
        Guid customerId, LoyaltyAdjustmentDirection direction, LoyaltyAdjustmentRequest request, CancellationToken cancellationToken)
    {
        var stored = await _dbContext.LoyaltyAdjustments.AsNoTracking()
            .SingleOrDefaultAsync(adjustment => adjustment.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (stored is null)
        {
            return null;
        }

        return stored.CustomerId == customerId && stored.Direction == direction && stored.Points == request.Points
            ? await GetWalletAsync(customerId, cancellationToken)
            : throw new ConflictException("Esta clave ya se usó para otro ajuste. Recarga e intenta de nuevo.");
    }

    private static LoyaltyAdjustmentDirection ValidateAdjustment(LoyaltyAdjustmentRequest request, out string reason)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        LoyaltyAdjustmentDirection? direction = Enum.GetNames<LoyaltyAdjustmentDirection>()
            .FirstOrDefault(name => string.Equals(name, request.Direction?.Trim(), StringComparison.OrdinalIgnoreCase)) is { } name
            ? Enum.Parse<LoyaltyAdjustmentDirection>(name)
            : null;
        if (direction is null)
        {
            errors["direction"] = ["Indica si el ajuste abona (Credit) o descuenta (Debit) puntos."];
        }

        if (request.Points is < 1 or > LoyaltySettings.MaxPointsPerItem)
        {
            errors["points"] = [$"Los puntos deben estar entre 1 y {LoyaltySettings.MaxPointsPerItem}."];
        }

        reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < LoyaltyAdjustment.MinReasonLength or > LoyaltyAdjustment.MaxReasonLength)
        {
            errors["reason"] = [$"El motivo debe tener entre {LoyaltyAdjustment.MinReasonLength} y {LoyaltyAdjustment.MaxReasonLength} caracteres."];
        }

        if (request.IdempotencyKey == Guid.Empty)
        {
            errors["idempotencyKey"] = ["Falta la clave de la operación; recarga la página e intenta de nuevo."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }

        return direction!.Value;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    internal async Task<bool> IsProgramEnabledAsync(CancellationToken cancellationToken)
        => await _dbContext.LoyaltySettings.AsNoTracking()
            .AnyAsync(settings => settings.Id == LoyaltySettings.SingletonId && settings.IsEnabled, cancellationToken);

    private async Task<int> ValidityDaysAsync(CancellationToken cancellationToken)
        => await _dbContext.LoyaltySettings.AsNoTracking()
            .Where(settings => settings.Id == LoyaltySettings.SingletonId)
            .Select(settings => settings.PointsValidityDays)
            .SingleAsync(cancellationToken);

    internal async Task<IReadOnlyList<LoyaltyLot>> TrackedUsableLotsAsync(Guid customerId, DateTime utcNow, CancellationToken cancellationToken)
    {
        var today = BogotaDate(utcNow);
        return (await TrackedAsync(_dbContext.LoyaltyLots, lot => lot.CustomerId == customerId && lot.RemainingPoints > 0, cancellationToken))
            .Where(lot => lot.UsablePointsOn(today) > 0)
            .OrderBy(lot => lot.ExpiresOn).ThenBy(lot => lot.AccruedAt).ThenBy(lot => lot.Id)
            .ToList();
    }

    /// <summary>Tracked rows from the database plus the ones added in this unit of work and not saved yet.</summary>
    private static async Task<List<T>> TrackedAsync<T>(
        DbSet<T> set, System.Linq.Expressions.Expression<Func<T, bool>> predicate, CancellationToken cancellationToken)
        where T : class
    {
        var stored = await set.Where(predicate).ToListAsync(cancellationToken);
        var compiled = predicate.Compile();
        var added = set.Local.Where(entity => compiled(entity)).Where(entity => !stored.Contains(entity));
        return stored.Concat(added).ToList();
    }

    private static bool IsOriginalOf(LoyaltyLot lot, LoyaltySourceType sourceType, Guid sourceId)
        => lot.SourceType == sourceType && lot.SourceId == sourceId && lot.OriginLotId is null;

    private async Task<Dictionary<Guid, string>> SourceLabelsAsync(IReadOnlyCollection<LoyaltyLot> lots, CancellationToken cancellationToken)
    {
        var recordIds = lots.Where(lot => lot.SourceType == LoyaltySourceType.ServiceRecord).Select(lot => lot.SourceId).Distinct().ToArray();
        var membershipIds = lots.Where(lot => lot.SourceType == LoyaltySourceType.Membership).Select(lot => lot.SourceId).Distinct().ToArray();
        var services = recordIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await _dbContext.ServiceRecords.AsNoTracking()
                .Where(record => recordIds.Contains(record.Id))
                .ToDictionaryAsync(record => record.Id, record => record.ApprovedServiceNameSnapshot ?? record.ServiceNameSnapshot, cancellationToken);
        var plans = membershipIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await _dbContext.CustomerMemberships.AsNoTracking()
                .Where(membership => membershipIds.Contains(membership.Id))
                .ToDictionaryAsync(membership => membership.Id, membership => membership.PlanName, cancellationToken);

        return lots.ToDictionary(
            lot => lot.Id,
            lot => lot.SourceType switch
            {
                LoyaltySourceType.ServiceRecord => $"Servicio: {services.GetValueOrDefault(lot.SourceId, "servicio realizado")}",
                LoyaltySourceType.Membership => $"Membresía: {plans.GetValueOrDefault(lot.SourceId, "membresía")}",
                _ => "Ajuste manual"
            });
    }

    private void EnsureTransaction()
    {
        if (_dbContext.Database.IsNpgsql() && _dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Wallet changes must run inside the caller's transaction.");
        }
    }

    private DateOnly Today() => BogotaDate(_timeProvider.GetUtcNow().UtcDateTime);

    internal static DateOnly BogotaDate(DateTime utc) => DateOnly.FromDateTime(BogotaClock.ToLocal(utc));
}
