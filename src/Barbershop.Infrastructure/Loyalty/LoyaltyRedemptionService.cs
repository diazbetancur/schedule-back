using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Common.Paging;
using Barbershop.Application.Loyalty.Redemptions;
using Barbershop.Application.ServiceRecords;
using Barbershop.Domain.Loyalty;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Barbershop.Infrastructure.Loyalty;

/// <summary>
/// Rewards and redemptions (ADR-0015). A request reserves its points (soonest expiry first, per lot); a delivery
/// consumes that reservation; a rejection, cancellation or expiry releases it, once. Every change to a redemption
/// takes, in this order, the lock of its customer profile and then the redemption's row (after a service record's,
/// when the delivery is a service record confirmation): service record → customer profiles → redemptions, the same
/// global order as the wallet, merges, annulments and the expiration job.
/// </summary>
internal sealed class LoyaltyRedemptionService : IAdminLoyaltyRewardsService, IAdminLoyaltyRedemptionsService, IMyLoyaltyRedemptionsService
{

    private const string StaleMessage = "La solicitud de canje cambió mientras la revisabas. Recarga e intenta de nuevo.";
    private const string RewardConflictMessage = "La recompensa fue modificada por otra persona. Recarga e intenta de nuevo.";

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly LoyaltyWalletService _wallet;

    public LoyaltyRedemptionService(AppDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _wallet = new LoyaltyWalletService(dbContext, timeProvider);
    }

    // ── Catalog ────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<LoyaltyRewardView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rewards = await _dbContext.LoyaltyRewards.AsNoTracking()
            .OrderByDescending(reward => reward.IsActive).ThenBy(reward => reward.Name)
            .ToListAsync(cancellationToken);
        return await MapRewardsAsync(rewards, cancellationToken);
    }

    public async Task<IReadOnlyList<LoyaltyRewardServiceOption>> GetServiceOptionsAsync(CancellationToken cancellationToken = default)
        => await (
                from service in _dbContext.Services.AsNoTracking()
                join general in _dbContext.GeneralServices.AsNoTracking() on service.GeneralServiceId equals general.Id
                where !service.IsDeleted
                orderby general.Name, service.Name
                select new LoyaltyRewardServiceOption(service.Id, service.Name, service.IsActive, general.Id, general.Name))
            .ToListAsync(cancellationToken);

    public async Task<LoyaltyRewardView> CreateAsync(Guid currentUserId, LoyaltyRewardUpsertRequest request, CancellationToken cancellationToken = default)
    {
        var (name, description, kind) = await ValidateRewardAsync(request, requireVersion: false, cancellationToken);
        var reward = new LoyaltyReward(
            name, description, kind, request.ServiceId, request.PointsCost, currentUserId, _timeProvider.GetUtcNow().UtcDateTime);
        _dbContext.LoyaltyRewards.Add(reward);
        _dbContext.LoyaltyRewardChanges.Add(reward.CreationChange());
        if (!request.IsActive
            && reward.Update(name, description, kind, request.ServiceId, request.PointsCost, false, currentUserId, reward.CreatedAt) is { } deactivation)
        {
            _dbContext.LoyaltyRewardChanges.Add(deactivation);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return (await MapRewardsAsync([reward], cancellationToken))[0];
    }

    public async Task<LoyaltyRewardView> UpdateAsync(
        Guid currentUserId, Guid rewardId, LoyaltyRewardUpsertRequest request, CancellationToken cancellationToken = default)
    {
        var (name, description, kind) = await ValidateRewardAsync(request, requireVersion: true, cancellationToken);
        var reward = await _dbContext.LoyaltyRewards.SingleOrDefaultAsync(candidate => candidate.Id == rewardId, cancellationToken)
            ?? throw new NotFoundException("La recompensa no existe.");
        if (reward.Version != request.Version!.Value)
        {
            throw new ConflictException(RewardConflictMessage);
        }

        // Requests keep their own copy of name, kind, service and cost: nothing here changes them.
        var change = reward.Update(
            name, description, kind, request.ServiceId, request.PointsCost, request.IsActive, currentUserId, _timeProvider.GetUtcNow().UtcDateTime);
        if (change is not null)
        {
            _dbContext.Entry(reward).Property(x => x.Version).OriginalValue = request.Version.Value;
            _dbContext.LoyaltyRewardChanges.Add(change);
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new ConflictException(RewardConflictMessage);
            }
        }

        return (await MapRewardsAsync([reward], cancellationToken))[0];
    }

    public async Task<PagedResult<LoyaltyRewardChangeView>> GetChangesAsync(
        Guid rewardId, PageQuery? query = null, CancellationToken cancellationToken = default)
    {
        var page = (query ?? new PageQuery()).ToRequest();
        if (!await _dbContext.LoyaltyRewards.AnyAsync(reward => reward.Id == rewardId, cancellationToken))
        {
            throw new NotFoundException("La recompensa no existe.");
        }

        return await _dbContext.LoyaltyRewardChanges.AsNoTracking()
            .Where(change => change.RewardId == rewardId)
            .OrderByDescending(change => change.ChangedAt).ThenByDescending(change => change.ChangeType).ThenBy(change => change.Id)
            .ToPagedResultAsync(page, MapRewardChangesAsync, cancellationToken);
    }

    private async Task<IReadOnlyList<LoyaltyRewardChangeView>> MapRewardChangesAsync(
        IReadOnlyList<LoyaltyRewardChange> changes, CancellationToken cancellationToken)
    {
        var users = await UserNamesAsync(_dbContext, changes.Select(change => change.ChangedByUserId), cancellationToken);
        var serviceIds = changes.SelectMany(change => new[] { change.PreviousServiceId, change.NewServiceId })
            .Where(id => id is not null).Select(id => id!.Value).Distinct().ToArray();
        var services = await _dbContext.Services.AsNoTracking()
            .Where(service => serviceIds.Contains(service.Id))
            .ToDictionaryAsync(service => service.Id, service => service.Name, cancellationToken);

        return changes.Select(change => new LoyaltyRewardChangeView(
                change.Id,
                change.ChangeType.ToString(),
                change.ChangedAt,
                users.GetValueOrDefault(change.ChangedByUserId),
                change.PreviousName,
                change.NewName,
                change.PreviousDescription,
                change.NewDescription,
                change.PreviousKind?.ToString(),
                change.NewKind.ToString(),
                change.PreviousServiceId is { } previousService ? services.GetValueOrDefault(previousService) : null,
                change.NewServiceId is { } newService ? services.GetValueOrDefault(newService) : null,
                change.PreviousPointsCost,
                change.NewPointsCost,
                change.PreviousIsActive,
                change.NewIsActive))
            .ToArray();
    }

    private async Task<(string Name, string? Description, LoyaltyRewardKind Kind)> ValidateRewardAsync(
        LoyaltyRewardUpsertRequest request, bool requireVersion, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < LoyaltyReward.MinNameLength or > LoyaltyReward.MaxNameLength)
        {
            errors["name"] = [$"El nombre debe tener entre {LoyaltyReward.MinNameLength} y {LoyaltyReward.MaxNameLength} caracteres."];
        }

        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description is { Length: > LoyaltyReward.MaxDescriptionLength })
        {
            errors["description"] = [$"La descripción debe tener {LoyaltyReward.MaxDescriptionLength} caracteres o menos."];
        }

        var kind = ParseName<LoyaltyRewardKind>(request.Kind);
        if (kind is null)
        {
            errors["kind"] = ["Elige el tipo de recompensa: un servicio del catálogo (Service) u otra (Other)."];
        }
        else if (kind == LoyaltyRewardKind.Service)
        {
            if (request.ServiceId is not { } serviceId || serviceId == Guid.Empty)
            {
                errors["serviceId"] = ["Elige el servicio que se entrega como recompensa."];
            }
            else if (!await _dbContext.Services.AnyAsync(service => service.Id == serviceId && !service.IsDeleted, cancellationToken))
            {
                errors["serviceId"] = ["El servicio no existe en el catálogo."];
            }
        }
        else if (request.ServiceId is not null)
        {
            errors["serviceId"] = ["Solo una recompensa de tipo servicio lleva un servicio asociado."];
        }

        if (request.PointsCost is < 1 or > LoyaltySettings.MaxPointsPerItem)
        {
            errors["pointsCost"] = [$"El coste debe ser un número entero de puntos entre 1 y {LoyaltySettings.MaxPointsPerItem}."];
        }

        if (requireVersion && request.Version is null)
        {
            errors["version"] = ["Falta la versión de la recompensa. Recarga e intenta de nuevo."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }

        return (name, description, kind!.Value);
    }

    private async Task<IReadOnlyList<LoyaltyRewardView>> MapRewardsAsync(IReadOnlyList<LoyaltyReward> rewards, CancellationToken cancellationToken)
    {
        var serviceIds = rewards.Where(reward => reward.ServiceId is not null).Select(reward => reward.ServiceId!.Value).Distinct().ToArray();
        var services = await _dbContext.Services.AsNoTracking()
            .Where(service => serviceIds.Contains(service.Id))
            .ToDictionaryAsync(service => service.Id, service => service.Name, cancellationToken);
        var users = await UserNamesAsync(
            _dbContext, rewards.Where(reward => reward.UpdatedByUserId is not null).Select(reward => reward.UpdatedByUserId!.Value), cancellationToken);
        return rewards.Select(reward => new LoyaltyRewardView(
                reward.Id,
                reward.Name,
                reward.Description,
                reward.Kind.ToString(),
                reward.ServiceId,
                reward.ServiceId is { } serviceId ? services.GetValueOrDefault(serviceId) : null,
                reward.PointsCost,
                reward.IsActive,
                reward.Version,
                reward.CreatedAt,
                reward.UpdatedAt,
                reward.UpdatedByUserId is { } userId ? users.GetValueOrDefault(userId) : null))
            .ToArray();
    }

    // ── Customer ───────────────────────────────────────────────────────────────────────────────

    public async Task<MyLoyaltyCatalogView> GetCatalogAsync(Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var programEnabled = await _wallet.IsProgramEnabledAsync(cancellationToken);
        var profileId = await OwnProfileIdAsync(currentUserId, cancellationToken);
        var available = profileId is { } customerId
            ? Math.Max(0, await _wallet.AvailablePointsAsync(customerId, _timeProvider.GetUtcNow().UtcDateTime, cancellationToken))
            : 0;
        _dbContext.ChangeTracker.Clear();

        var rewards = await (
                from reward in _dbContext.LoyaltyRewards.AsNoTracking()
                join service in _dbContext.Services.AsNoTracking() on reward.ServiceId equals service.Id into joined
                from service in joined.DefaultIfEmpty()
                where reward.IsActive && (reward.ServiceId == null || (service != null && service.IsActive && !service.IsDeleted))
                orderby reward.PointsCost, reward.Name
                select new { reward, ServiceName = service == null ? null : service.Name })
            .ToListAsync(cancellationToken);

        return new MyLoyaltyCatalogView(
            profileId is not null,
            programEnabled,
            available,
            rewards.Select(row => new MyLoyaltyRewardView(
                    row.reward.Id, row.reward.Name, row.reward.Description, row.reward.Kind.ToString(), row.ServiceName,
                    row.reward.PointsCost, programEnabled && profileId is not null && row.reward.PointsCost <= available))
                .ToArray());
    }

    public async Task<PagedResult<MyLoyaltyRedemptionView>> ListMineAsync(
        Guid currentUserId, PageQuery? query = null, CancellationToken cancellationToken = default)
    {
        var page = (query ?? new PageQuery()).ToRequest();
        if (await OwnProfileIdAsync(currentUserId, cancellationToken) is not { } customerId)
        {
            return new PagedResult<MyLoyaltyRedemptionView>([], page.Page, page.PageSize, 0);
        }

        // The live profile and every profile merged into it (their redemptions were final when merged). Pending ones
        // (requested or approved) come first so none is pushed off the first page, then newest request first.
        var profileIds = await ProfileAndMergedSourcesAsync(customerId, cancellationToken);
        var today = Today();
        return await _dbContext.LoyaltyRedemptions.AsNoTracking()
            .Where(redemption => profileIds.Contains(redemption.CustomerId))
            .OrderBy(redemption =>
                redemption.Status == LoyaltyRedemptionStatus.Requested || redemption.Status == LoyaltyRedemptionStatus.Approved ? 0 : 1)
            .ThenByDescending(redemption => redemption.RequestedAt)
            .ThenByDescending(redemption => redemption.Id)
            .ToPagedResultAsync(page, redemption => MapMine(redemption, today), cancellationToken);
    }

    public async Task<MyLoyaltyRedemptionView> RequestAsync(
        Guid currentUserId, LoyaltyRedemptionRequest request, CancellationToken cancellationToken = default)
    {
        ValidateKeys(request.RewardId, request.IdempotencyKey);
        var profileId = await OwnProfileIdAsync(currentUserId, cancellationToken)
            ?? throw new ConflictException("Necesitas un perfil de cliente vinculado a tu cuenta para canjear puntos.");

        var redemptionId = await CreateAsync(
            currentUserId, profileId, request.RewardId, request.IdempotencyKey, LoyaltyRedemptionChannel.App,
            inPersonVerified: false, approveNow: false, cancellationToken);
        return MapMine(await LoadAsync(redemptionId, cancellationToken), Today());
    }

    public async Task<MyLoyaltyRedemptionView> CancelMineAsync(
        Guid currentUserId, Guid redemptionId, MyLoyaltyRedemptionCancelRequest request, CancellationToken cancellationToken = default)
    {
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason is { Length: > LoyaltyRedemption.MaxReasonLength })
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["reason"] = [$"El motivo debe tener {LoyaltyRedemption.MaxReasonLength} caracteres o menos."]
            });
        }

        var profileId = await OwnProfileIdAsync(currentUserId, cancellationToken);
        var owner = await _dbContext.LoyaltyRedemptions.AsNoTracking()
            .Where(redemption => redemption.Id == redemptionId)
            .Select(redemption => (Guid?)redemption.CustomerId)
            .SingleOrDefaultAsync(cancellationToken);
        if (profileId is null || owner != profileId)
        {
            // Never reveal another customer's redemption.
            throw new NotFoundException("La solicitud de canje no existe.");
        }

        await ChangeAsync(
            redemptionId,
            expectedVersion: null,
            isReplay: redemption => redemption.Status == LoyaltyRedemptionStatus.Cancelled && redemption.CancelledByCustomer,
            apply: async (redemption, utcNow, _) =>
            {
                EnsurePending(redemption, "cancelar");
                redemption.Cancel(reason, currentUserId, byCustomer: true, utcNow);
                await _wallet.ReleaseReservationInCurrentTransactionAsync(redemption.CustomerId, redemption.Id, currentUserId, utcNow, cancellationToken);
            },
            cancellationToken);
        return MapMine(await LoadAsync(redemptionId, cancellationToken), Today());
    }

    // ── Staff ──────────────────────────────────────────────────────────────────────────────────

    public async Task<PagedResult<LoyaltyRedemptionView>> ListAsync(LoyaltyRedemptionListQuery listQuery, CancellationToken cancellationToken = default)
    {
        var page = PageRequest.Create(listQuery.Page, listQuery.PageSize);
        var (status, customerId) = (listQuery.Status, listQuery.CustomerId);
        LoyaltyRedemptionStatus? parsed = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            parsed = ParseName<LoyaltyRedemptionStatus>(status)
                ?? throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["status"] = ["Estado no válido: Requested, Approved, Delivered, Rejected, Cancelled o Expired."]
                });
        }

        // Requested and approved are work queues: no default date, so an old pending redemption is never hidden. Any
        // other view is history: the last 30 days by default and at most 30 days per query.
        var range = parsed is { } queue && LoyaltyRedemption.IsPendingStatus(queue)
            ? HistoryRange.ResolveOptional(listQuery.From, listQuery.To)
            : HistoryRange.Resolve(listQuery.From, listQuery.To, Today());

        var query = _dbContext.LoyaltyRedemptions.AsNoTracking();
        if (parsed is { } wanted)
        {
            query = query.Where(redemption => redemption.Status == wanted);
        }

        if (customerId is { } customer)
        {
            query = query.Where(redemption => redemption.CustomerId == customer);
        }

        if (range?.From is { } from)
        {
            var startUtc = PagingExtensions.StartOfBogotaDayUtc(from);
            query = query.Where(redemption => redemption.RequestedAt >= startUtc);
        }

        if (range?.To is { } to)
        {
            var endUtc = PagingExtensions.EndOfBogotaDayUtc(to);
            query = query.Where(redemption => redemption.RequestedAt < endUtc);
        }

        var ordered = parsed is { } pending && LoyaltyRedemption.IsPendingStatus(pending)
            ? query.OrderBy(redemption => redemption.RequestedAt).ThenBy(redemption => redemption.Id)
            : query.OrderByDescending(redemption => redemption.RequestedAt).ThenByDescending(redemption => redemption.Id);
        var today = Today();
        return await ordered.ToPagedResultAsync(
            page, (redemptions, ct) => MapAsync(_dbContext, redemptions, today, ct), cancellationToken);
    }

    public async Task<IReadOnlyList<LoyaltyRewardView>> ListActiveRewardsAsync(CancellationToken cancellationToken = default)
    {
        var rewards = await _dbContext.LoyaltyRewards.AsNoTracking()
            .Where(reward => reward.IsActive)
            .OrderBy(reward => reward.PointsCost).ThenBy(reward => reward.Name)
            .ToListAsync(cancellationToken);
        return await MapRewardsAsync(rewards, cancellationToken);
    }

    public async Task<PagedResult<LoyaltyRedemptionView>> ListDeliveriesAsync(
        PageQuery? query = null, CancellationToken cancellationToken = default)
    {
        var page = (query ?? new PageQuery()).ToRequest();
        var today = Today();
        return await _dbContext.LoyaltyRedemptions.AsNoTracking()
            .Where(redemption => redemption.Status == LoyaltyRedemptionStatus.Approved)
            .OrderBy(redemption => redemption.DeliverBy).ThenBy(redemption => redemption.RequestedAt).ThenBy(redemption => redemption.Id)
            .ToPagedResultAsync(page, (redemptions, ct) => MapAsync(_dbContext, redemptions, today, ct), cancellationToken);
    }

    public async Task<LoyaltyRedemptionView> GetAsync(Guid redemptionId, CancellationToken cancellationToken = default)
        => (await MapAsync(_dbContext, [await LoadAsync(redemptionId, cancellationToken)], Today(), cancellationToken))[0];

    public async Task<LoyaltyRedemptionView> ApproveAsync(
        Guid currentUserId, Guid redemptionId, LoyaltyRedemptionDecisionRequest request, CancellationToken cancellationToken = default)
    {
        RequireVersion(request.Version);
        await ChangeAsync(
            redemptionId,
            request.Version,
            isReplay: redemption => redemption.Status == LoyaltyRedemptionStatus.Approved && redemption.ApprovedByUserId == currentUserId,
            apply: (redemption, utcNow, today) =>
            {
                if (redemption.Status != LoyaltyRedemptionStatus.Requested)
                {
                    throw new ConflictException(NotInStatusMessage(redemption, "aprobar"));
                }

                // Revalidated here even if the expiration job has not run yet.
                EnsureWithinDeadline(redemption, today, "aprobar");
                redemption.Approve(currentUserId, utcNow, today);
                return Task.CompletedTask;
            },
            cancellationToken);
        return await GetAsync(redemptionId, cancellationToken);
    }

    public async Task<LoyaltyRedemptionView> RejectAsync(
        Guid currentUserId, Guid redemptionId, LoyaltyRedemptionReasonRequest request, CancellationToken cancellationToken = default)
    {
        var reason = ValidateReason(request);
        await ChangeAsync(
            redemptionId,
            request.Version,
            isReplay: redemption => redemption.Status == LoyaltyRedemptionStatus.Rejected && redemption.RejectionReason == reason,
            apply: async (redemption, utcNow, _) =>
            {
                if (redemption.Status != LoyaltyRedemptionStatus.Requested)
                {
                    throw new ConflictException(NotInStatusMessage(redemption, "rechazar"));
                }

                redemption.Reject(reason, currentUserId, utcNow);
                await _wallet.ReleaseReservationInCurrentTransactionAsync(redemption.CustomerId, redemption.Id, currentUserId, utcNow, cancellationToken);
            },
            cancellationToken);
        return await GetAsync(redemptionId, cancellationToken);
    }

    public async Task<LoyaltyRedemptionView> CancelAsync(
        Guid currentUserId, Guid redemptionId, LoyaltyRedemptionReasonRequest request, CancellationToken cancellationToken = default)
    {
        var reason = ValidateReason(request);
        await ChangeAsync(
            redemptionId,
            request.Version,
            isReplay: redemption => redemption.Status == LoyaltyRedemptionStatus.Cancelled
                && !redemption.CancelledByCustomer && redemption.CancellationReason == reason,
            apply: async (redemption, utcNow, _) =>
            {
                EnsurePending(redemption, "cancelar");
                redemption.Cancel(reason, currentUserId, byCustomer: false, utcNow);
                await _wallet.ReleaseReservationInCurrentTransactionAsync(redemption.CustomerId, redemption.Id, currentUserId, utcNow, cancellationToken);
            },
            cancellationToken);
        return await GetAsync(redemptionId, cancellationToken);
    }

    public async Task<LoyaltyRedemptionView> DeliverAsync(
        Guid currentUserId, Guid redemptionId, LoyaltyRedemptionDecisionRequest request, CancellationToken cancellationToken = default)
    {
        RequireVersion(request.Version);
        await ChangeAsync(
            redemptionId,
            request.Version,
            isReplay: redemption => redemption.Status == LoyaltyRedemptionStatus.Delivered && redemption.ServiceRecordId is null,
            apply: async (redemption, utcNow, today) =>
            {
                if (redemption.RewardKindSnapshot == LoyaltyRewardKind.Service)
                {
                    // No direct delivery can skip the service record (ADR-0015 R13).
                    throw new ConflictException(
                        "Esta recompensa es un servicio: se entrega al confirmar el servicio realizado con el tipo Recompensa.");
                }

                if (redemption.Status != LoyaltyRedemptionStatus.Approved)
                {
                    throw new ConflictException(NotInStatusMessage(redemption, "entregar"));
                }

                EnsureWithinDeadline(redemption, today, "entregar");
                redemption.Deliver(currentUserId, utcNow, today, serviceRecordId: null);
                await _wallet.ConsumeReservationInCurrentTransactionAsync(redemption.CustomerId, redemption.Id, currentUserId, utcNow, cancellationToken);
            },
            cancellationToken);
        return await GetAsync(redemptionId, cancellationToken);
    }

    public async Task<LoyaltyRedemptionView> CreateAtCounterAsync(
        Guid currentUserId, Guid customerId, LoyaltyCounterRedemptionRequest request, CancellationToken cancellationToken = default)
    {
        ValidateKeys(request.RewardId, request.IdempotencyKey);
        if (!request.InPersonVerified)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["inPersonVerified"] = ["Confirma que verificaste en persona la identidad del cliente."]
            });
        }

        if (!await _dbContext.Customers.AnyAsync(customer => customer.Id == customerId, cancellationToken))
        {
            throw new NotFoundException("El cliente no existe.");
        }

        var redemptionId = await CreateAsync(
            currentUserId, customerId, request.RewardId, request.IdempotencyKey, LoyaltyRedemptionChannel.Counter,
            inPersonVerified: true, request.ApproveNow, cancellationToken);
        return await GetAsync(redemptionId, cancellationToken);
    }

    // ── Service records (R13) ──────────────────────────────────────────────────────────────────

    internal async Task<IReadOnlyList<ServiceRecordRedemptionOption>> GetServiceRedemptionOptionsAsync(
        Guid customerId, Guid? serviceId, CancellationToken cancellationToken)
    {
        var live = await _wallet.ResolveLiveCustomerIdAsync(customerId, cancellationToken);
        var query = _dbContext.LoyaltyRedemptions.AsNoTracking()
            .Where(redemption => redemption.CustomerId == live
                && redemption.Status == LoyaltyRedemptionStatus.Approved
                && redemption.RewardKindSnapshot == LoyaltyRewardKind.Service);
        if (serviceId is { } wanted)
        {
            query = query.Where(redemption => redemption.ServiceIdSnapshot == wanted);
        }

        var today = Today();
        return (await query.OrderBy(redemption => redemption.DeliverBy).ToListAsync(cancellationToken))
            .Select(redemption => new ServiceRecordRedemptionOption(
                redemption.Id,
                redemption.RewardNameSnapshot,
                redemption.ServiceIdSnapshot!.Value,
                redemption.ServiceNameSnapshot!,
                redemption.PointsCost,
                redemption.ApprovedAt!.Value,
                redemption.DeliverBy!.Value,
                redemption.IsPastDeadlineOn(today)))
            .ToArray();
    }

    /// <summary>
    /// Delivers an approved service redemption through the confirmation of <paramref name="serviceRecordId"/>, inside
    /// the caller's transaction, which already holds the record's lock: then the customer profile and the redemption
    /// are locked, the redemption is checked against the confirmed customer and service and its delivery deadline,
    /// and its reservation is consumed.
    /// </summary>
    internal async Task<LoyaltyRedemption> DeliverServiceRewardInCurrentTransactionAsync(
        Guid redemptionId, Guid customerId, Guid serviceId, Guid serviceRecordId, Guid currentUserId, DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var live = await _wallet.LockLiveCustomerAsync(customerId, cancellationToken);
        await LockRedemptionAsync(redemptionId, cancellationToken);
        var redemption = await _dbContext.LoyaltyRedemptions.SingleOrDefaultAsync(candidate => candidate.Id == redemptionId, cancellationToken);

        static ValidationProblemException Invalid(string message) => new(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["loyaltyRedemptionId"] = [message]
        });

        if (redemption is null || redemption.CustomerId != live)
        {
            throw Invalid("La solicitud de canje no existe o no es del cliente confirmado.");
        }

        if (redemption.RewardKindSnapshot != LoyaltyRewardKind.Service)
        {
            throw Invalid("Esa recompensa no es un servicio: se entrega desde la bandeja de canjes.");
        }

        if (redemption.ServiceIdSnapshot != serviceId)
        {
            throw Invalid($"La recompensa es el servicio «{redemption.ServiceNameSnapshot}»: confirma ese servicio.");
        }

        if (redemption.Status != LoyaltyRedemptionStatus.Approved)
        {
            throw new ConflictException(NotInStatusMessage(redemption, "entregar"));
        }

        var today = LoyaltyWalletService.BogotaDate(utcNow);
        EnsureWithinDeadline(redemption, today, "entregar");
        redemption.Deliver(currentUserId, utcNow, today, serviceRecordId);
        await _wallet.ConsumeReservationInCurrentTransactionAsync(live, redemption.Id, currentUserId, utcNow, cancellationToken);
        return redemption;
    }

    // ── Expiration job ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records as expired the pending redemptions past their deadline and releases their points (which expire at once
    /// if their lots did). One profile per transaction, under its lock; idempotent.
    /// </summary>
    internal async Task<int> ExpireDueRedemptionsAsync(CancellationToken cancellationToken)
    {
        var today = Today();
        var customerIds = await DueRedemptions(_dbContext.LoyaltyRedemptions.AsNoTracking(), today)
            .Select(redemption => redemption.CustomerId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var expired = 0;
        foreach (var customerId in customerIds)
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            await _wallet.LockCustomersAsync([customerId], cancellationToken);
            if (_dbContext.Database.IsNpgsql())
            {
                await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM public.loyalty_redemptions WHERE \"CustomerId\" = {customerId} AND \"Status\" IN ('Requested', 'Approved') ORDER BY \"Id\" FOR UPDATE",
                    cancellationToken);
            }

            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
            var due = await DueRedemptions(_dbContext.LoyaltyRedemptions.Where(redemption => redemption.CustomerId == customerId), today)
                .OrderBy(redemption => redemption.Id)
                .ToListAsync(cancellationToken);
            foreach (var redemption in due)
            {
                redemption.Expire(utcNow, today);
                await _wallet.ReleaseReservationInCurrentTransactionAsync(customerId, redemption.Id, performedByUserId: null, utcNow, cancellationToken);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _dbContext.ChangeTracker.Clear();
            expired += due.Count;
        }

        return expired;
    }

    private static IQueryable<LoyaltyRedemption> DueRedemptions(IQueryable<LoyaltyRedemption> query, DateOnly today)
        => query.Where(redemption =>
            (redemption.Status == LoyaltyRedemptionStatus.Requested && redemption.ApproveBy < today)
            || (redemption.Status == LoyaltyRedemptionStatus.Approved && redemption.DeliverBy < today));

    // ── Merges (ADR-0013 D5) ───────────────────────────────────────────────────────────────────

    /// <summary>Pending redemptions of either profile (they block the merge) and a fingerprint of all of them.</summary>
    internal async Task<(int SourcePending, int TargetPending, string Fingerprint)> PendingForMergeAsync(
        Guid sourceId, Guid targetId, CancellationToken cancellationToken)
    {
        var rows = await _dbContext.LoyaltyRedemptions.AsNoTracking()
            .Where(redemption => (redemption.CustomerId == sourceId || redemption.CustomerId == targetId)
                && (redemption.Status == LoyaltyRedemptionStatus.Requested || redemption.Status == LoyaltyRedemptionStatus.Approved))
            .Select(redemption => new { redemption.Id, redemption.CustomerId, redemption.Status, redemption.Version })
            .ToListAsync(cancellationToken);
        var fingerprint = string.Join(',', rows.OrderBy(row => row.Id).Select(row => $"R{row.Id}:{row.CustomerId}:{row.Status}:{row.Version}"));
        return (rows.Count(row => row.CustomerId == sourceId), rows.Count(row => row.CustomerId == targetId), fingerprint);
    }

    // ── Shared steps ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a redemption and reserves its points under the profile's lock. A retried request (same person and key)
    /// returns the stored one; the same key for another reward or profile is a conflict.
    /// </summary>
    private async Task<Guid> CreateAsync(
        Guid currentUserId,
        Guid customerId,
        Guid rewardId,
        Guid idempotencyKey,
        LoyaltyRedemptionChannel channel,
        bool inPersonVerified,
        bool approveNow,
        CancellationToken cancellationToken)
    {
        if (await ReplayAsync(currentUserId, idempotencyKey, customerId, rewardId, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        try
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            await _wallet.LockCustomersAsync([customerId], cancellationToken);
            if (!await _dbContext.Customers.AsNoTracking().AnyAsync(customer => customer.Id == customerId && customer.MergedIntoCustomerId == null, cancellationToken))
            {
                throw new ConflictException("El cliente fue fusionado en otro perfil; registra el canje en el perfil que se conserva.");
            }

            // The same key stored while this request waited for the lock.
            if (await ReplayAsync(currentUserId, idempotencyKey, customerId, rewardId, cancellationToken) is { } concurrent)
            {
                return concurrent;
            }

            if (!await _wallet.IsProgramEnabledAsync(cancellationToken))
            {
                throw new ConflictException("El programa de fidelización está deshabilitado: no se aceptan canjes nuevos.");
            }

            var reward = await _dbContext.LoyaltyRewards.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == rewardId, cancellationToken)
                ?? throw new NotFoundException("La recompensa no existe.");
            if (!reward.IsActive)
            {
                throw new ConflictException("La recompensa ya no está disponible.");
            }

            string? serviceName = null;
            if (reward.ServiceId is { } serviceId)
            {
                var service = await _dbContext.Services.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == serviceId, cancellationToken);
                if (service is null || service.IsDeleted || !service.IsActive)
                {
                    throw new ConflictException("El servicio de esta recompensa ya no está disponible.");
                }

                serviceName = service.Name;
            }

            var settings = await _dbContext.LoyaltySettings.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == LoyaltySettings.SingletonId, cancellationToken);
            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
            var today = LoyaltyWalletService.BogotaDate(utcNow);
            var redemption = new LoyaltyRedemption(
                customerId, reward, serviceName, channel, inPersonVerified, idempotencyKey, currentUserId, utcNow, today,
                settings.RedemptionApprovalDays, settings.RedemptionDeliveryDays);
            _dbContext.LoyaltyRedemptions.Add(redemption);
            await _wallet.ReserveInCurrentTransactionAsync(customerId, redemption.Id, redemption.PointsCost, currentUserId, utcNow, cancellationToken);
            if (approveNow)
            {
                redemption.Approve(currentUserId, utcNow, today);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _dbContext.ChangeTracker.Clear();
            return redemption.Id;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            _dbContext.ChangeTracker.Clear();
            return await ReplayAsync(currentUserId, idempotencyKey, customerId, rewardId, cancellationToken)
                ?? throw new ConflictException("La solicitud de canje ya se registró. Recarga e intenta de nuevo.");
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("Los puntos del cliente cambiaron mientras se registraba el canje. Recarga e intenta de nuevo.");
        }
    }

    private async Task<Guid?> ReplayAsync(Guid requestedByUserId, Guid idempotencyKey, Guid customerId, Guid rewardId, CancellationToken cancellationToken)
    {
        var stored = await _dbContext.LoyaltyRedemptions.AsNoTracking()
            .Where(redemption => redemption.RequestedByUserId == requestedByUserId && redemption.IdempotencyKey == idempotencyKey)
            .Select(redemption => new { redemption.Id, redemption.CustomerId, redemption.RewardId })
            .SingleOrDefaultAsync(cancellationToken);
        if (stored is null)
        {
            return null;
        }

        return stored.CustomerId == customerId && stored.RewardId == rewardId
            ? stored.Id
            : throw new ConflictException("Esta clave ya se usó para otra solicitud de canje. Recarga e intenta de nuevo.");
    }

    /// <summary>
    /// One state change: profile lock → redemption row lock → checks → change, in one transaction. Repeating the same
    /// decision returns the stored one; a stale version or a different decision on a closed redemption is a conflict.
    /// </summary>
    private async Task ChangeAsync(
        Guid redemptionId,
        uint? expectedVersion,
        Func<LoyaltyRedemption, bool> isReplay,
        Func<LoyaltyRedemption, DateTime, DateOnly, Task> apply,
        CancellationToken cancellationToken)
    {
        var customerId = await _dbContext.LoyaltyRedemptions.AsNoTracking()
            .Where(redemption => redemption.Id == redemptionId)
            .Select(redemption => (Guid?)redemption.CustomerId)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("La solicitud de canje no existe.");

        try
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            await _wallet.LockCustomersAsync([customerId], cancellationToken);
            await LockRedemptionAsync(redemptionId, cancellationToken);
            var redemption = await _dbContext.LoyaltyRedemptions.SingleAsync(candidate => candidate.Id == redemptionId, cancellationToken);
            if (isReplay(redemption))
            {
                return;
            }

            if (expectedVersion is { } version && redemption.Version != version)
            {
                throw new ConflictException(StaleMessage);
            }

            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
            await apply(redemption, utcNow, LoyaltyWalletService.BogotaDate(utcNow));
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(StaleMessage);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException(StaleMessage);
        }
        finally
        {
            _dbContext.ChangeTracker.Clear();
        }
    }

    private static void EnsurePending(LoyaltyRedemption redemption, string action)
    {
        if (!redemption.IsPending)
        {
            throw new ConflictException(NotInStatusMessage(redemption, action));
        }
    }

    private static void EnsureWithinDeadline(LoyaltyRedemption redemption, DateOnly today, string action)
    {
        if (redemption.IsPastDeadlineOn(today))
        {
            throw new ConflictException(
                $"No se puede {action}: el plazo terminó el {redemption.CurrentDeadline:dd/MM/yyyy}. La solicitud se cerrará como vencida y sus puntos se liberarán.");
        }
    }

    private static string NotInStatusMessage(LoyaltyRedemption redemption, string action)
        => $"No se puede {action} esta solicitud de canje: está {StatusLabel(redemption.Status)}. Recarga la bandeja.";

    private static string StatusLabel(LoyaltyRedemptionStatus status) => status switch
    {
        LoyaltyRedemptionStatus.Requested => "pendiente de aprobación",
        LoyaltyRedemptionStatus.Approved => "aprobada",
        LoyaltyRedemptionStatus.Delivered => "entregada",
        LoyaltyRedemptionStatus.Rejected => "rechazada",
        LoyaltyRedemptionStatus.Cancelled => "cancelada",
        _ => "vencida"
    };

    private static string ValidateReason(LoyaltyRedemptionReasonRequest request)
    {
        var reason = request.Reason?.Trim() ?? string.Empty;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (reason.Length is < LoyaltyRedemption.MinReasonLength or > LoyaltyRedemption.MaxReasonLength)
        {
            errors["reason"] = [$"El motivo debe tener entre {LoyaltyRedemption.MinReasonLength} y {LoyaltyRedemption.MaxReasonLength} caracteres."];
        }

        if (request.Version is null)
        {
            errors["version"] = ["Falta la versión de la solicitud. Recarga e intenta de nuevo."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }

        return reason;
    }

    private static void RequireVersion(uint? version)
    {
        if (version is null)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["version"] = ["Falta la versión de la solicitud. Recarga e intenta de nuevo."]
            });
        }
    }

    private static void ValidateKeys(Guid rewardId, Guid idempotencyKey)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (rewardId == Guid.Empty)
        {
            errors["rewardId"] = ["Elige la recompensa."];
        }

        if (idempotencyKey == Guid.Empty)
        {
            errors["idempotencyKey"] = ["Falta la clave de la operación; recarga la página e intenta de nuevo."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }
    }

    private async Task LockRedemptionAsync(Guid redemptionId, CancellationToken cancellationToken)
    {
        if (_dbContext.Database.IsNpgsql())
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM public.loyalty_redemptions WHERE \"Id\" = {redemptionId} FOR UPDATE", cancellationToken);
        }
    }

    private async Task<LoyaltyRedemption> LoadAsync(Guid redemptionId, CancellationToken cancellationToken)
        => await _dbContext.LoyaltyRedemptions.AsNoTracking().SingleOrDefaultAsync(redemption => redemption.Id == redemptionId, cancellationToken)
            ?? throw new NotFoundException("La solicitud de canje no existe.");

    private async Task<Guid?> OwnProfileIdAsync(Guid currentUserId, CancellationToken cancellationToken)
        => await _dbContext.Customers.AsNoTracking()
            .Where(customer => customer.UserId == currentUserId && customer.MergedIntoCustomerId == null)
            .Select(customer => (Guid?)customer.Id)
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<List<Guid>> ProfileAndMergedSourcesAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var all = new List<Guid> { customerId };
        var frontier = new List<Guid> { customerId };
        for (var depth = 0; depth < LoyaltyWalletService.MaxMergeChainDepth && frontier.Count > 0; depth++)
        {
            var current = frontier;
            frontier = await _dbContext.Customers.AsNoTracking()
                .Where(customer => customer.MergedIntoCustomerId != null && current.Contains(customer.MergedIntoCustomerId.Value))
                .Select(customer => customer.Id)
                .ToListAsync(cancellationToken);
            frontier = frontier.Except(all).ToList();
            all.AddRange(frontier);
        }

        return all;
    }

    private DateOnly Today() => LoyaltyWalletService.BogotaDate(_timeProvider.GetUtcNow().UtcDateTime);

    private static MyLoyaltyRedemptionView MapMine(LoyaltyRedemption redemption, DateOnly today) => new(
        redemption.Id,
        redemption.RewardNameSnapshot,
        redemption.RewardKindSnapshot.ToString(),
        redemption.ServiceNameSnapshot,
        redemption.PointsCost,
        redemption.Status.ToString(),
        redemption.RequestedAt,
        redemption.ApproveBy,
        redemption.ApprovedAt,
        redemption.DeliverBy,
        redemption.DeliveredAt,
        redemption.RejectedAt,
        redemption.RejectionReason,
        redemption.CancelledAt,
        redemption.CancelledByCustomer,
        redemption.ExpiredAt,
        redemption.IsPastDeadlineOn(today),
        redemption.IsPending);

    internal static async Task<IReadOnlyList<LoyaltyRedemptionView>> MapAsync(
        AppDbContext dbContext, IReadOnlyList<LoyaltyRedemption> redemptions, DateOnly today, CancellationToken cancellationToken)
    {
        if (redemptions.Count == 0)
        {
            return [];
        }

        var ids = redemptions.Select(redemption => redemption.Id).ToArray();
        var allocations = await (
                from allocation in dbContext.LoyaltyRedemptionAllocations.AsNoTracking()
                join lot in dbContext.LoyaltyLots.AsNoTracking() on allocation.LotId equals lot.Id
                where ids.Contains(allocation.RedemptionId)
                select new { allocation.RedemptionId, allocation.LotId, allocation.Points, lot.ExpiresOn })
            .ToListAsync(cancellationToken);
        var customerIds = redemptions.Select(redemption => redemption.CustomerId).Distinct().ToArray();
        var customers = await dbContext.Customers.AsNoTracking()
            .Where(customer => customerIds.Contains(customer.Id))
            .ToDictionaryAsync(customer => customer.Id, customer => customer.FullName, cancellationToken);
        var users = await UserNamesAsync(
            dbContext,
            redemptions.SelectMany(redemption => new[]
                {
                    redemption.RequestedByUserId, redemption.ApprovedByUserId, redemption.DeliveredByUserId,
                    redemption.RejectedByUserId, redemption.CancelledByUserId
                })
                .Where(id => id is not null).Select(id => id!.Value),
            cancellationToken);
        string? Name(Guid? userId) => userId is { } id ? users.GetValueOrDefault(id) : null;

        return redemptions.Select(redemption => new LoyaltyRedemptionView(
                redemption.Id,
                redemption.CustomerId,
                customers.GetValueOrDefault(redemption.CustomerId),
                redemption.RewardId,
                redemption.RewardNameSnapshot,
                redemption.RewardKindSnapshot.ToString(),
                redemption.ServiceIdSnapshot,
                redemption.ServiceNameSnapshot,
                redemption.PointsCost,
                redemption.Status.ToString(),
                redemption.Channel.ToString(),
                redemption.InPersonVerified,
                redemption.RequestedAt,
                Name(redemption.RequestedByUserId),
                redemption.RequestedOn,
                redemption.ApprovalDays,
                redemption.DeliveryDays,
                redemption.ApproveBy,
                redemption.ApprovedAt,
                Name(redemption.ApprovedByUserId),
                redemption.DeliverBy,
                redemption.DeliveredAt,
                Name(redemption.DeliveredByUserId),
                redemption.ServiceRecordId,
                redemption.RejectedAt,
                Name(redemption.RejectedByUserId),
                redemption.RejectionReason,
                redemption.CancelledAt,
                Name(redemption.CancelledByUserId),
                redemption.CancellationReason,
                redemption.CancelledByCustomer,
                redemption.ExpiredAt,
                redemption.IsPastDeadlineOn(today),
                allocations.Where(allocation => allocation.RedemptionId == redemption.Id)
                    .OrderBy(allocation => allocation.ExpiresOn)
                    .Select(allocation => new LoyaltyRedemptionAllocationView(allocation.LotId, allocation.Points, allocation.ExpiresOn))
                    .ToArray(),
                redemption.Version))
            .ToArray();
    }

    private static async Task<Dictionary<Guid, string>> UserNamesAsync(
        AppDbContext dbContext, IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToArray();
        return ids.Length == 0
            ? []
            : await dbContext.Users.AsNoTracking()
                .Where(user => ids.Contains(user.Id))
                .ToDictionaryAsync(user => user.Id, user => user.FullName, cancellationToken);
    }

    /// <summary>Accepts only the enum's names (any casing), never numeric strings.</summary>
    private static TEnum? ParseName<TEnum>(string? value)
        where TEnum : struct, Enum
    {
        var name = Enum.GetNames<TEnum>().FirstOrDefault(candidate =>
            string.Equals(candidate, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        return name is null ? null : Enum.Parse<TEnum>(name);
    }
}
