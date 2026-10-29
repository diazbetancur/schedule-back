using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Domain.Common;
using Barbershop.Domain.Memberships;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Barbershop.Infrastructure.Memberships;

/// <summary>
/// Consumptions and reversals of a sold membership. Both only append rows (consumption, movements, reversal);
/// balances are always recomputed from the movements. Each operation locks the membership row, so concurrent
/// consumptions are applied one after another and can never take more than the available balance.
/// Consuming never creates income, professional commissions or touches appointments.
/// </summary>
internal sealed class MembershipLedgerService : IAdminMembershipLedgerService
{
    private const int ReasonMinLength = 3;
    private const int ReasonMaxLength = 500;

    private readonly AppDbContext _dbContext;
    private readonly IAdminMembershipsService _memberships;
    private readonly TimeProvider _timeProvider;

    public MembershipLedgerService(AppDbContext dbContext, IAdminMembershipsService memberships, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _memberships = memberships;
        _timeProvider = timeProvider;
    }

    public async Task<CustomerMembershipDetailView> ConsumeAsync(
        Guid currentUserId,
        Guid membershipId,
        MembershipConsumptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateShape(request);

        // A retried request (same key) returns what was already registered instead of consuming again.
        if (await FindByKeyAsync(request.IdempotencyKey, cancellationToken) is { } previous)
        {
            return await ReplayAsync(previous, membershipId, request, cancellationToken);
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
                    return await ReplayAsync(concurrent, membershipId, request, cancellationToken);
                }

                var staffExists = request.StaffProfileId is not { } staffId
                    || await _dbContext.StaffProfiles.AnyAsync(staff => staff.Id == staffId, cancellationToken);
                Register(currentUserId, membership, request, await LoadAvailableAsync(membership, cancellationToken), staffExists);
                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The same key reached another membership's lock at the same time; the stored one wins.
            _dbContext.ChangeTracker.Clear();
            var stored = await FindByKeyAsync(request.IdempotencyKey, cancellationToken) ?? throw new ConflictException(
                "El consumo no se pudo registrar por una operación simultánea. Recarga e intenta de nuevo.");
            return await ReplayAsync(stored, membershipId, request, cancellationToken);
        }

        return await _memberships.GetByIdAsync(membershipId, cancellationToken);
    }

    public async Task<CustomerMembershipDetailView> ReverseAsync(
        Guid currentUserId,
        Guid membershipId,
        Guid consumptionId,
        MembershipConsumptionReverseRequest request,
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

            var consumption = await _dbContext.MembershipConsumptions.SingleOrDefaultAsync(
                    candidate => candidate.Id == consumptionId && candidate.CustomerMembershipId == membershipId,
                    cancellationToken)
                ?? throw new NotFoundException("El consumo no existe en esta membresía.");

            if (await _dbContext.MembershipConsumptionReversals.AnyAsync(
                    reversal => reversal.MembershipConsumptionId == consumption.Id, cancellationToken))
            {
                throw new ConflictException("Este consumo ya fue revertido.");
            }

            // Allowed on an annulled membership too: the units go back to their lines but the membership stays
            // annulled, so they remain blocked. The original consumption and its movements are kept as they are.
            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
            _dbContext.MembershipConsumptionReversals.Add(new MembershipConsumptionReversal(consumption.Id, reason, currentUserId, utcNow));
            var taken = await _dbContext.MembershipMovements
                .Where(movement => movement.MembershipConsumptionId == consumption.Id && movement.Type == MembershipMovementType.Consumption)
                .ToListAsync(cancellationToken);
            foreach (var movement in taken)
            {
                _dbContext.MembershipMovements.Add(new MembershipMovement(
                    membershipId, movement.MembershipAllowanceId, consumption.Id, MembershipMovementType.Reversal, movement.Quantity, currentUserId, utcNow));
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            _dbContext.ChangeTracker.Clear();
            throw new ConflictException("Este consumo ya fue revertido.");
        }

        return await _memberships.GetByIdAsync(membershipId, cancellationToken);
    }

    private static void ValidateShape(MembershipConsumptionRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.ServiceId == Guid.Empty)
        {
            errors["serviceId"] = ["Elige el servicio que se prestó."];
        }

        if (request.Quantity is < 1 or > MembershipConsumption.MaxQuantity)
        {
            errors["quantity"] = [$"La cantidad debe ser un número entero entre 1 y {MembershipConsumption.MaxQuantity}."];
        }

        if (request.ServiceDate == default)
        {
            errors["serviceDate"] = ["Indica la fecha real del servicio."];
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

    private void Register(
        Guid currentUserId,
        CustomerMembership membership,
        MembershipConsumptionRequest request,
        IReadOnlyDictionary<Guid, int> available,
        bool staffExists)
    {
        if (membership.Status == MembershipStatus.PendingActivation)
        {
            throw new ConflictException("La membresía está pendiente de activación; actívala antes de registrar consumos.");
        }

        if (membership.Status == MembershipStatus.Annulled)
        {
            throw new ConflictException("La membresía está anulada; sus beneficios están bloqueados.");
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var today = DateOnly.FromDateTime(BogotaClock.ToLocal(_timeProvider.GetUtcNow().UtcDateTime));
        if (request.ServiceDate > today)
        {
            errors["serviceDate"] = ["La fecha del servicio no puede ser futura."];
        }
        else if (request.ServiceDate < membership.StartsOn || request.ServiceDate > membership.EndsOn)
        {
            // Retroactive registrations are fine, as long as the service happened within the validity.
            errors["serviceDate"] =
                [$"La fecha del servicio debe estar dentro de la vigencia ({membership.StartsOn:yyyy-MM-dd} al {membership.EndsOn:yyyy-MM-dd})."];
        }

        // Gifted units are used first, then purchased ones; within each, in the line order of the sale.
        var lines = membership.Allowances
            .Where(allowance => allowance.Services.Any(service => service.ServiceId == request.ServiceId))
            .OrderBy(allowance => allowance.Origin == MembershipAllowanceOrigin.Gift ? 0 : 1)
            .ThenBy(allowance => allowance.Position)
            .ToList();
        if (lines.Count == 0)
        {
            errors["serviceId"] = ["El servicio no está incluido en esta membresía."];
        }
        else
        {
            var total = lines.Sum(line => available[line.Id]);
            if (request.Quantity > total)
            {
                errors["quantity"] = [$"Saldo insuficiente: quedan {total} disponible(s) para este servicio."];
            }
        }

        if (!staffExists)
        {
            errors["staffProfileId"] = ["El profesional no existe."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }

        var serviceName = lines[0].Services.Single(service => service.ServiceId == request.ServiceId).ServiceNameSnapshot;
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var consumption = new MembershipConsumption(
            membership.Id, request.ServiceId, serviceName, request.Quantity, request.ServiceDate,
            request.StaffProfileId, request.IdempotencyKey, currentUserId, utcNow);
        _dbContext.MembershipConsumptions.Add(consumption);

        // One operation, possibly spread over a gift line and a purchased line.
        var remaining = request.Quantity;
        foreach (var line in lines)
        {
            var take = Math.Min(remaining, available[line.Id]);
            if (take == 0)
            {
                continue;
            }

            _dbContext.MembershipMovements.Add(new MembershipMovement(
                membership.Id, line.Id, consumption.Id, MembershipMovementType.Consumption, take, currentUserId, utcNow));
            remaining -= take;
            if (remaining == 0)
            {
                break;
            }
        }
    }

    private async Task<IReadOnlyDictionary<Guid, int>> LoadAvailableAsync(CustomerMembership membership, CancellationToken cancellationToken)
    {
        var used = await _dbContext.MembershipMovements
            .Where(movement => movement.CustomerMembershipId == membership.Id)
            .GroupBy(movement => movement.MembershipAllowanceId)
            .Select(group => new
            {
                AllowanceId = group.Key,
                Used = group.Sum(movement => movement.Type == MembershipMovementType.Consumption ? movement.Quantity : -movement.Quantity),
            })
            .ToDictionaryAsync(row => row.AllowanceId, row => row.Used, cancellationToken);

        return membership.Allowances.ToDictionary(
            allowance => allowance.Id,
            allowance => allowance.GrantedQuantity - used.GetValueOrDefault(allowance.Id));
    }

    private async Task<CustomerMembershipDetailView> ReplayAsync(
        MembershipConsumption previous,
        Guid membershipId,
        MembershipConsumptionRequest request,
        CancellationToken cancellationToken)
    {
        var sameRequest = previous.CustomerMembershipId == membershipId
            && previous.ServiceId == request.ServiceId
            && previous.Quantity == request.Quantity
            && previous.ServiceDate == request.ServiceDate
            && previous.StaffProfileId == request.StaffProfileId;
        if (!sameRequest)
        {
            throw new ConflictException("Esta operación ya se registró con otros datos. Recarga la página para registrar un consumo nuevo.");
        }

        return await _memberships.GetByIdAsync(membershipId, cancellationToken);
    }

    private Task<MembershipConsumption?> FindByKeyAsync(Guid idempotencyKey, CancellationToken cancellationToken)
        => _dbContext.MembershipConsumptions.AsNoTracking()
            .SingleOrDefaultAsync(consumption => consumption.IdempotencyKey == idempotencyKey, cancellationToken);

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
            .Include(membership => membership.Allowances)
            .ThenInclude(allowance => allowance.Services)
            .AsSplitQuery()
            .SingleOrDefaultAsync(membership => membership.Id == membershipId, cancellationToken)
            ?? throw new NotFoundException("La membresía no existe.");
    }
}
