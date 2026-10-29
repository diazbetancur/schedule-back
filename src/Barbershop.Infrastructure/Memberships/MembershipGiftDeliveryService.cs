using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Domain.Common;
using Barbershop.Domain.Memberships;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Barbershop.Infrastructure.Memberships;

/// <summary>
/// Deliveries of physical gifts and their reversals. Both only append rows; whether a gift is delivered is
/// derived from them. Each operation locks the membership row, and the (gift, delivery number) uniqueness
/// stops two simultaneous deliveries of one gift even without the lock. Delivering touches no balances,
/// income, expenses, commissions or appointments.
/// </summary>
internal sealed class MembershipGiftDeliveryService : IAdminMembershipGiftDeliveryService
{
    private const int ReasonMinLength = 3;
    private const int ReasonMaxLength = 500;

    private readonly AppDbContext _dbContext;
    private readonly IAdminMembershipsService _memberships;
    private readonly TimeProvider _timeProvider;

    public MembershipGiftDeliveryService(AppDbContext dbContext, IAdminMembershipsService memberships, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _memberships = memberships;
        _timeProvider = timeProvider;
    }

    public async Task<CustomerMembershipDetailView> DeliverAsync(
        Guid currentUserId,
        Guid membershipId,
        Guid giftId,
        MembershipGiftDeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateShape(request);

        // A retried request (same key) returns what was already registered instead of delivering again.
        if (await FindByKeyAsync(request.IdempotencyKey, cancellationToken) is { } previous)
        {
            return await ReplayAsync(previous, membershipId, giftId, request, cancellationToken);
        }

        try
        {
            await using (var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken))
            {
                await LockMembershipAsync(membershipId, cancellationToken);
                var membership = await LoadAsync(membershipId, cancellationToken);

                // A concurrent retry may have committed while this request waited for the lock.
                if (await FindByKeyAsync(request.IdempotencyKey, cancellationToken) is { } concurrent)
                {
                    return await ReplayAsync(concurrent, membershipId, giftId, request, cancellationToken);
                }

                var gift = membership.Gifts.SingleOrDefault(candidate => candidate.Id == giftId)
                    ?? throw new NotFoundException("El obsequio no existe en esta membresía.");
                var previousDeliveries = await _dbContext.MembershipGiftDeliveries
                    .Where(delivery => delivery.MembershipGiftId == gift.Id)
                    .Select(delivery => new
                    {
                        delivery.Id,
                        Reversed = _dbContext.MembershipGiftDeliveryReversals.Any(reversal => reversal.MembershipGiftDeliveryId == delivery.Id),
                    })
                    .ToListAsync(cancellationToken);

                EnsureDeliverable(membership, gift, previousDeliveries.Any(delivery => !delivery.Reversed), request.DeliveredOn);

                _dbContext.MembershipGiftDeliveries.Add(new MembershipGiftDelivery(
                    gift, previousDeliveries.Count + 1, request.DeliveredOn, request.IdempotencyKey,
                    currentUserId, _timeProvider.GetUtcNow().UtcDateTime));
                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Either the same key or another delivery of this gift won the race.
            _dbContext.ChangeTracker.Clear();
            var stored = await FindByKeyAsync(request.IdempotencyKey, cancellationToken)
                ?? throw new ConflictException("El obsequio ya fue entregado por otra operación simultánea.");
            return await ReplayAsync(stored, membershipId, giftId, request, cancellationToken);
        }

        return await _memberships.GetByIdAsync(membershipId, cancellationToken);
    }

    public async Task<CustomerMembershipDetailView> ReverseAsync(
        Guid currentUserId,
        Guid membershipId,
        Guid giftId,
        Guid deliveryId,
        MembershipGiftDeliveryReverseRequest request,
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

        try
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            await LockMembershipAsync(membershipId, cancellationToken);
            await LoadAsync(membershipId, cancellationToken);

            var delivery = await _dbContext.MembershipGiftDeliveries.SingleOrDefaultAsync(
                    candidate => candidate.Id == deliveryId
                        && candidate.MembershipGiftId == giftId
                        && candidate.CustomerMembershipId == membershipId,
                    cancellationToken)
                ?? throw new NotFoundException("La entrega no existe para este obsequio.");

            if (await _dbContext.MembershipGiftDeliveryReversals.AnyAsync(
                    reversal => reversal.MembershipGiftDeliveryId == delivery.Id, cancellationToken))
            {
                throw new ConflictException("Esta entrega ya fue revertida.");
            }

            // Allowed on an annulled membership too: the gift then becomes not deliverable, it is not re-enabled.
            _dbContext.MembershipGiftDeliveryReversals.Add(new MembershipGiftDeliveryReversal(
                delivery.Id, reason, currentUserId, _timeProvider.GetUtcNow().UtcDateTime));
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            _dbContext.ChangeTracker.Clear();
            throw new ConflictException("Esta entrega ya fue revertida.");
        }

        return await _memberships.GetByIdAsync(membershipId, cancellationToken);
    }

    private static void ValidateShape(MembershipGiftDeliveryRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.DeliveredOn == default)
        {
            errors["deliveredOn"] = ["Indica la fecha real de la entrega."];
        }

        if (request.IdempotencyKey == Guid.Empty)
        {
            errors["idempotencyKey"] = ["Falta la clave de la operación; recarga la página e intenta de nuevo."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }
    }

    private void EnsureDeliverable(CustomerMembership membership, MembershipGift gift, bool alreadyDelivered, DateOnly deliveredOn)
    {
        if (gift.Kind != MembershipGiftKind.Physical)
        {
            throw new ConflictException("Solo los obsequios físicos se entregan; los obsequios de servicio se usan registrando consumos.");
        }

        if (membership.Status == MembershipStatus.Annulled)
        {
            throw new ConflictException("La membresía está anulada; sus obsequios pendientes no se pueden entregar.");
        }

        if (membership.Status == MembershipStatus.PendingActivation)
        {
            throw new ConflictException("La membresía está pendiente de activación; actívala antes de entregar sus obsequios.");
        }

        if (alreadyDelivered)
        {
            throw new ConflictException("El obsequio ya fue entregado. Revierte la entrega si fue un error.");
        }

        var today = DateOnly.FromDateTime(BogotaClock.ToLocal(_timeProvider.GetUtcNow().UtcDateTime));
        if (deliveredOn > today)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["deliveredOn"] = ["La fecha de entrega no puede ser futura."]
            });
        }

        if (deliveredOn < membership.SoldOn)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["deliveredOn"] = [$"La fecha de entrega no puede ser anterior a la venta ({membership.SoldOn:yyyy-MM-dd})."]
            });
        }
    }

    private async Task<CustomerMembershipDetailView> ReplayAsync(
        MembershipGiftDelivery previous,
        Guid membershipId,
        Guid giftId,
        MembershipGiftDeliveryRequest request,
        CancellationToken cancellationToken)
    {
        var sameRequest = previous.CustomerMembershipId == membershipId
            && previous.MembershipGiftId == giftId
            && previous.DeliveredOn == request.DeliveredOn;
        if (!sameRequest)
        {
            throw new ConflictException("Esta operación ya se registró con otros datos. Recarga la página para registrar una entrega nueva.");
        }

        return await _memberships.GetByIdAsync(membershipId, cancellationToken);
    }

    private Task<MembershipGiftDelivery?> FindByKeyAsync(Guid idempotencyKey, CancellationToken cancellationToken)
        => _dbContext.MembershipGiftDeliveries.AsNoTracking()
            .SingleOrDefaultAsync(delivery => delivery.IdempotencyKey == idempotencyKey, cancellationToken);

    /// <summary>Serializes the operations on one membership with a PostgreSQL row lock; other providers skip it.</summary>
    private async Task LockMembershipAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        if (_dbContext.Database.IsNpgsql())
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM public.customer_memberships WHERE \"Id\" = {membershipId} FOR UPDATE",
                cancellationToken);
        }
    }

    private async Task<CustomerMembership> LoadAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        return await _dbContext.CustomerMemberships
            .Include(membership => membership.Gifts)
            .SingleOrDefaultAsync(membership => membership.Id == membershipId, cancellationToken)
            ?? throw new NotFoundException("La membresía no existe.");
    }
}
