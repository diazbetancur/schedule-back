using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Loyalty.Admin;
using Barbershop.Domain.Loyalty;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Loyalty;

internal sealed class LoyaltySettingsService : IAdminLoyaltySettingsService
{
    private const int MaxChangesListed = 50;

    private const string ConcurrencyConflictMessage =
        "La configuración de fidelización fue modificada por otra persona. Recarga para ver los valores actuales e intenta de nuevo.";

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public LoyaltySettingsService(AppDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<LoyaltySettingsView> GetAsync(CancellationToken cancellationToken = default)
    {
        var settings = await LoadAsync(asTracking: false, cancellationToken);
        return await MapAsync(settings, cancellationToken);
    }

    public async Task<LoyaltySettingsView> UpdateAsync(
        Guid changedByUserId, LoyaltySettingsUpdateRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);

        var settings = await LoadAsync(asTracking: true, cancellationToken);
        if (settings.Version != request.Version!.Value)
        {
            throw new ConflictException(ConcurrencyConflictMessage);
        }

        var change = settings.Update(
            request.IsEnabled, request.PointsValidityDays, changedByUserId, _timeProvider.GetUtcNow().UtcDateTime);
        if (change is null)
        {
            return await MapAsync(settings, cancellationToken);
        }

        // Compared again by the UPDATE itself, so two requests that read the same version cannot both win.
        _dbContext.Entry(settings).Property(x => x.Version).OriginalValue = request.Version.Value;
        _dbContext.LoyaltySettingsChanges.Add(change);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(ConcurrencyConflictMessage);
        }

        return await MapAsync(settings, cancellationToken);
    }

    public async Task<IReadOnlyList<LoyaltySettingsChangeView>> GetChangesAsync(CancellationToken cancellationToken = default)
    {
        var changes = await _dbContext.LoyaltySettingsChanges
            .AsNoTracking()
            .OrderByDescending(change => change.ChangedAt)
            .Take(MaxChangesListed)
            .ToListAsync(cancellationToken);

        var names = await LoadUserNamesAsync(changes.Select(change => change.ChangedByUserId), cancellationToken);
        return changes
            .Select(change => new LoyaltySettingsChangeView(
                change.Id,
                change.ChangedAt,
                change.ChangedByUserId,
                names.GetValueOrDefault(change.ChangedByUserId),
                change.PreviousIsEnabled,
                change.NewIsEnabled,
                change.PreviousPointsValidityDays,
                change.NewPointsValidityDays))
            .ToArray();
    }

    private static void Validate(LoyaltySettingsUpdateRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request.PointsValidityDays is < LoyaltySettings.MinPointsValidityDays or > LoyaltySettings.MaxPointsValidityDays)
        {
            errors["pointsValidityDays"] =
            [
                $"La vigencia de los puntos debe ser un número entero de días entre {LoyaltySettings.MinPointsValidityDays} y {LoyaltySettings.MaxPointsValidityDays}."
            ];
        }

        if (request.Version is null)
        {
            errors["version"] = ["Falta la versión de la configuración. Recarga la página e intenta de nuevo."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }
    }

    private async Task<LoyaltySettings> LoadAsync(bool asTracking, CancellationToken cancellationToken)
    {
        var query = asTracking ? _dbContext.LoyaltySettings : _dbContext.LoyaltySettings.AsNoTracking();

        // The row is created by the migration that introduced loyalty settings.
        return await query.SingleOrDefaultAsync(settings => settings.Id == LoyaltySettings.SingletonId, cancellationToken)
            ?? throw new NotFoundException("La configuración de fidelización no está inicializada.");
    }

    private async Task<LoyaltySettingsView> MapAsync(LoyaltySettings settings, CancellationToken cancellationToken)
    {
        string? updatedByName = null;
        if (settings.UpdatedByUserId is { } userId)
        {
            var names = await LoadUserNamesAsync([userId], cancellationToken);
            updatedByName = names.GetValueOrDefault(userId);
        }

        return new LoyaltySettingsView(
            settings.IsEnabled, settings.PointsValidityDays, settings.Version, settings.UpdatedAt, updatedByName);
    }

    private async Task<Dictionary<Guid, string>> LoadUserNamesAsync(IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        return await _dbContext.Users
            .AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.FullName, cancellationToken);
    }
}
