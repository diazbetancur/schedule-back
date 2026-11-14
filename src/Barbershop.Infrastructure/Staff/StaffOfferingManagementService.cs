using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Staff.Admin;
using Barbershop.Domain.Staff;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Staff;

/// <summary>
/// Administration of the general services each professional offers and their duration (ADR-0016 B2, D6). A replace
/// runs in one transaction holding the professional's row (the same lock booking takes), then share-locks the general
/// services it adds or activates and revalidates them, so an offering is never added for a service being deactivated.
/// </summary>
internal sealed class StaffOfferingManagementService : IAdminStaffOfferingsService
{
    public const int MaxOfferings = 100;

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public StaffOfferingManagementService(AppDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<StaffServiceOfferingsView> GetAsync(Guid staffProfileId, CancellationToken cancellationToken = default)
    {
        var staff = await LoadStaffAsync(staffProfileId, cancellationToken);
        return await MapAsync(staff, cancellationToken);
    }

    public async Task<StaffServiceOfferingsView> ReplaceAsync(
        Guid staffProfileId, StaffServiceOfferingsReplaceRequest request, CancellationToken cancellationToken = default)
    {
        var requested = Validate(request);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _dbContext.LockStaffProfileAsync(staffProfileId, cancellationToken);
        var staff = await LoadStaffAsync(staffProfileId, cancellationToken);

        var current = await _dbContext.StaffServiceOfferings
            .Where(offering => offering.StaffProfileId == staffProfileId)
            .ToListAsync(cancellationToken);
        var currentByService = current.ToDictionary(offering => offering.GeneralServiceId);

        // Offerings that become active (new and active, or reactivated) need an active general service. Their general
        // services are share-locked (in id order, after the professional) and then read, so a concurrent deactivation
        // either committed before (and is seen) or waits for this transaction.
        var activating = requested
            .Where(item => item.IsActive && !(currentByService.TryGetValue(item.GeneralServiceId, out var existing) && existing.IsActive))
            .Select(item => item.GeneralServiceId)
            .Order()
            .ToArray();
        foreach (var generalServiceId in activating)
        {
            await _dbContext.ShareGeneralServiceAsync(generalServiceId, cancellationToken);
        }

        var requestedIds = requested.Select(item => item.GeneralServiceId).ToArray();
        var services = await _dbContext.GeneralServices.AsNoTracking()
            .Where(service => requestedIds.Contains(service.Id))
            .ToDictionaryAsync(service => service.Id, cancellationToken);

        // BUG-079: an offering the professional already had for a general service deleted since is dropped (it is no
        // longer offered and nothing references an offering), instead of blocking every save with a 422. Adding a
        // deleted or unknown general service is still refused.
        requested = requested
            .Where(item => !(services.TryGetValue(item.GeneralServiceId, out var service)
                && service.IsDeleted
                && currentByService.ContainsKey(item.GeneralServiceId)))
            .ToList();
        requestedIds = requested.Select(item => item.GeneralServiceId).ToArray();
        activating = activating.Where(requestedIds.Contains).ToArray();

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        for (var index = 0; index < requested.Count; index++)
        {
            if (!services.TryGetValue(requested[index].GeneralServiceId, out var service) || service.IsDeleted)
            {
                errors[$"offerings[{index}].generalServiceId"] = ["El servicio general no existe o fue eliminado."];
            }
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }

        var inactive = activating.Where(id => !services[id].IsActive).Select(id => services[id].Name).ToArray();
        if (inactive.Length > 0)
        {
            throw new ConflictException(
                $"El servicio general \"{inactive[0]}\" está inactivo. Actívalo antes de ofrecerlo en la agenda del profesional.");
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var item in requested)
        {
            if (currentByService.TryGetValue(item.GeneralServiceId, out var existing))
            {
                existing.Update(item.DurationMinutes, item.IsActive, utcNow);
            }
            else
            {
                _dbContext.StaffServiceOfferings.Add(
                    new StaffServiceOffering(staffProfileId, item.GeneralServiceId, item.DurationMinutes, item.IsActive, utcNow));
            }
        }

        // Full replacement: whatever was not sent is no longer offered. Nothing references an offering (appointments
        // keep their own copy of the service and duration), so it is removed.
        _dbContext.StaffServiceOfferings.RemoveRange(current.Where(offering => !requestedIds.Contains(offering.GeneralServiceId)));

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await MapAsync(staff, cancellationToken);
    }

    private static IReadOnlyList<StaffServiceOfferingRequest> Validate(StaffServiceOfferingsReplaceRequest request)
    {
        var offerings = request?.Offerings ?? [];
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request?.Offerings is null)
        {
            errors["offerings"] = ["Envía la lista de servicios que ofrece el profesional (puede estar vacía)."];
        }
        else if (offerings.Count > MaxOfferings)
        {
            errors["offerings"] = [$"Un profesional puede ofrecer como máximo {MaxOfferings} servicios."];
        }

        var seen = new HashSet<Guid>();
        for (var index = 0; index < offerings.Count; index++)
        {
            var item = offerings[index];
            if (item is null || item.GeneralServiceId == Guid.Empty)
            {
                errors[$"offerings[{index}].generalServiceId"] = ["Elige el servicio general."];
                continue;
            }

            if (!seen.Add(item.GeneralServiceId))
            {
                errors[$"offerings[{index}].generalServiceId"] = ["No repitas un servicio general."];
            }

            if (!StaffServiceOffering.IsValidDuration(item.DurationMinutes))
            {
                errors[$"offerings[{index}].durationMinutes"] =
                    [$"La duración debe estar entre {StaffServiceOffering.MinDurationMinutes} y {StaffServiceOffering.MaxDurationMinutes} minutos."];
            }
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }

        return offerings;
    }

    private async Task<StaffProfile> LoadStaffAsync(Guid staffProfileId, CancellationToken cancellationToken)
        => await _dbContext.StaffProfiles.AsNoTracking()
               .SingleOrDefaultAsync(profile => profile.Id == staffProfileId, cancellationToken)
           ?? throw new KeyNotFoundException("No encontramos este profesional.");

    private async Task<StaffServiceOfferingsView> MapAsync(StaffProfile staff, CancellationToken cancellationToken)
    {
        var rows = await (
                from offering in _dbContext.StaffServiceOfferings.AsNoTracking()
                join service in _dbContext.GeneralServices.AsNoTracking() on offering.GeneralServiceId equals service.Id
                join category in _dbContext.ServiceCategories.AsNoTracking() on service.CategoryId equals category.Id
                // BUG-079: an offering of a deleted general service is not shown (it is dropped on the next save).
                where offering.StaffProfileId == staff.Id && !service.IsDeleted
                select new { offering, service, category })
            .ToListAsync(cancellationToken);

        var offerings = rows
            .OrderBy(row => row.category.SortOrder)
            .ThenBy(row => row.category.Name)
            .ThenBy(row => row.service.SortOrder)
            .ThenBy(row => row.service.Name)
            .Select(row => new StaffServiceOfferingView(
                row.service.Id,
                row.service.Name,
                row.category.Id,
                row.category.Name,
                row.offering.DurationMinutes,
                row.offering.IsActive,
                row.service.IsActive && !row.service.IsDeleted,
                row.category.IsActive && !row.category.IsDeleted,
                row.offering.CreatedAt,
                row.offering.UpdatedAt))
            .ToArray();

        return new StaffServiceOfferingsView(staff.Id, staff.DefaultAppointmentDurationMinutes, offerings);
    }
}
