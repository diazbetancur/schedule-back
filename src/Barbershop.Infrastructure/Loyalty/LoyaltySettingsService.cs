using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Common.Paging;
using Barbershop.Application.Loyalty.Admin;
using Barbershop.Domain.Loyalty;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Loyalty;

internal sealed class LoyaltySettingsService : IAdminLoyaltySettingsService
{

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
            request.IsEnabled, request.PointsValidityDays, changedByUserId, _timeProvider.GetUtcNow().UtcDateTime,
            request.RedemptionApprovalDays, request.RedemptionDeliveryDays);
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

    public async Task<PagedResult<LoyaltySettingsChangeView>> GetChangesAsync(
        PageQuery? query = null, CancellationToken cancellationToken = default)
    {
        var page = (query ?? new PageQuery()).ToRequest();
        return await _dbContext.LoyaltySettingsChanges
            .AsNoTracking()
            .OrderByDescending(change => change.ChangedAt)
            .ThenByDescending(change => change.Id)
            .ToPagedResultAsync(page, MapChangesAsync, cancellationToken);
    }

    private async Task<IReadOnlyList<LoyaltySettingsChangeView>> MapChangesAsync(
        IReadOnlyList<LoyaltySettingsChange> changes, CancellationToken cancellationToken)
    {

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
                change.NewPointsValidityDays,
                change.PreviousRedemptionApprovalDays,
                change.NewRedemptionApprovalDays,
                change.PreviousRedemptionDeliveryDays,
                change.NewRedemptionDeliveryDays))
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

        // Only requests made afterwards take new deadlines: each redemption keeps the ones applied (ADR-0015 R7).
        if (request.RedemptionApprovalDays is < LoyaltySettings.MinRedemptionDays or > LoyaltySettings.MaxRedemptionDays)
        {
            errors["redemptionApprovalDays"] =
                [$"El plazo para aprobar canjes debe ser un número entero de días entre {LoyaltySettings.MinRedemptionDays} y {LoyaltySettings.MaxRedemptionDays}."];
        }

        if (request.RedemptionDeliveryDays is < LoyaltySettings.MinRedemptionDays or > LoyaltySettings.MaxRedemptionDays)
        {
            errors["redemptionDeliveryDays"] =
                [$"El plazo para entregar canjes debe ser un número entero de días entre {LoyaltySettings.MinRedemptionDays} y {LoyaltySettings.MaxRedemptionDays}."];
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
            settings.IsEnabled, settings.PointsValidityDays, settings.Version, settings.UpdatedAt, updatedByName,
            settings.RedemptionApprovalDays, settings.RedemptionDeliveryDays);
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
