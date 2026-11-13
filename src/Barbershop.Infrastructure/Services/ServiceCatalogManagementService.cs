using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Services.Admin;
using Barbershop.Domain.Services;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Services;

/// <summary>
/// Masters of service categories and general services (ADR-0016 B1). Both are only deleted logically, and never
/// while something live depends on them, so the rates and the settled history keep their references. Every change
/// runs in a transaction with the locks of <see cref="ServiceCatalogLocks"/> and revalidates under them.
/// </summary>
internal sealed class ServiceCatalogManagementService : IAdminServiceCatalogService
{
    private const int MaxRelockAttempts = 3;

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public ServiceCatalogManagementService(AppDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    // ── Categories ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ServiceCategoryView>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var categories = await _dbContext.ServiceCategories.AsNoTracking()
            .Where(category => !category.IsDeleted)
            .OrderBy(category => category.SortOrder).ThenBy(category => category.Name)
            .ToListAsync(cancellationToken);
        var counts = await CountGeneralServicesAsync(categories.Select(category => category.Id).ToArray(), cancellationToken);
        return categories.Select(category => MapCategory(category, counts)).ToArray();
    }

    public async Task<ServiceCategoryView> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default)
        => await MapCategoryAsync(await LoadCategoryAsync(categoryId, cancellationToken), cancellationToken);

    public async Task<ServiceCategoryView> CreateCategoryAsync(ServiceCategoryCreateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateCategory(request.Name, request.SortOrder);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await EnsureCategoryNameIsFreeAsync(request.Name, null, cancellationToken);

        var category = new ServiceCategory(request.Name, request.SortOrder, UtcNow());
        _dbContext.ServiceCategories.Add(category);
        await _dbContext.SaveAndCommitAsync(transaction, cancellationToken);
        return await MapCategoryAsync(category, cancellationToken);
    }

    public async Task<ServiceCategoryView> UpdateCategoryAsync(
        Guid categoryId, ServiceCategoryUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateCategory(request.Name, request.SortOrder ?? 0);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _dbContext.LockCategoryAsync(categoryId, cancellationToken);
        var category = await LoadCategoryAsync(categoryId, cancellationToken);
        await EnsureCategoryNameIsFreeAsync(request.Name, categoryId, cancellationToken);

        category.Update(request.Name, request.SortOrder ?? category.SortOrder, UtcNow());
        await _dbContext.SaveAndCommitAsync(transaction, cancellationToken);
        return await MapCategoryAsync(category, cancellationToken);
    }

    public async Task<ServiceCategoryView> UpdateCategoryStatusAsync(
        Guid categoryId, ServiceCategoryStatusUpdateRequest request, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _dbContext.LockCategoryAsync(categoryId, cancellationToken);
        var category = await LoadCategoryAsync(categoryId, cancellationToken);
        if (request.IsActive)
        {
            category.Activate(UtcNow());
        }
        else
        {
            // Counted under the exclusive lock: a general service created, moved or reactivated meanwhile has
            // either committed already (and is counted) or waits for this transaction and then sees it inactive.
            var activeGeneralServices = await _dbContext.GeneralServices
                .CountAsync(service => service.CategoryId == categoryId && !service.IsDeleted && service.IsActive, cancellationToken);
            if (activeGeneralServices > 0)
            {
                throw new ConflictException(
                    $"La categoría tiene {activeGeneralServices} servicio(s) general(es) activo(s). Desactívalos o muévelos a otra categoría antes de desactivarla.");
            }

            category.Deactivate(UtcNow());
        }

        await _dbContext.SaveAndCommitAsync(transaction, cancellationToken);
        return await MapCategoryAsync(category, cancellationToken);
    }

    public async Task DeleteCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _dbContext.LockCategoryAsync(categoryId, cancellationToken);
        var category = await LoadCategoryAsync(categoryId, cancellationToken);
        var generalServices = await _dbContext.GeneralServices
            .CountAsync(service => service.CategoryId == categoryId && !service.IsDeleted, cancellationToken);
        if (generalServices > 0)
        {
            throw new ConflictException(
                $"La categoría tiene {generalServices} servicio(s) general(es). Muévelos a otra categoría o elimínalos antes de eliminarla.");
        }

        category.MarkDeleted(UtcNow());
        await _dbContext.SaveAndCommitAsync(transaction, cancellationToken);
    }

    // ── General services ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<GeneralServiceView>> GetGeneralServicesAsync(Guid? categoryId, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.GeneralServices.AsNoTracking().Where(service => !service.IsDeleted);
        if (categoryId is { } id)
        {
            query = query.Where(service => service.CategoryId == id);
        }

        var services = await query.ToListAsync(cancellationToken);
        var categories = await LoadCategoryLookupAsync(services.Select(service => service.CategoryId), cancellationToken);
        var rateCounts = await CountRatesAsync(services.Select(service => service.Id).ToArray(), cancellationToken);

        return services
            .OrderBy(service => categories.TryGetValue(service.CategoryId, out var category) ? category.SortOrder : int.MaxValue)
            .ThenBy(service => categories.TryGetValue(service.CategoryId, out var category) ? category.Name : string.Empty)
            .ThenBy(service => service.SortOrder)
            .ThenBy(service => service.Name)
            .Select(service => MapGeneralService(service, categories, rateCounts))
            .ToArray();
    }

    public async Task<GeneralServiceView> GetGeneralServiceAsync(Guid generalServiceId, CancellationToken cancellationToken = default)
        => await MapGeneralServiceAsync(await LoadGeneralServiceAsync(generalServiceId, cancellationToken), cancellationToken);

    public async Task<GeneralServiceView> CreateGeneralServiceAsync(GeneralServiceCreateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateGeneralService(request.CategoryId, request.Name, request.Description, request.SortOrder);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _dbContext.ShareCategoryAsync(request.CategoryId, cancellationToken);
        await EnsureCategoryCanReceiveAsync(request.CategoryId, cancellationToken);
        await EnsureGeneralServiceNameIsFreeAsync(request.CategoryId, request.Name, null, cancellationToken);

        var service = new GeneralService(
            request.CategoryId, request.Name, NormalizeDescription(request.Description), request.SortOrder, UtcNow());
        _dbContext.GeneralServices.Add(service);
        await _dbContext.SaveAndCommitAsync(transaction, cancellationToken);
        return await MapGeneralServiceAsync(service, cancellationToken);
    }

    public async Task<GeneralServiceView> UpdateGeneralServiceAsync(
        Guid generalServiceId, GeneralServiceUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateGeneralService(request.CategoryId, request.Name, request.Description, request.SortOrder ?? 0);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        // Category before general service (the global order); the target is locked even if it does not change.
        await _dbContext.ShareCategoryAsync(request.CategoryId, cancellationToken);
        await _dbContext.LockGeneralServiceAsync(generalServiceId, cancellationToken);
        var service = await LoadGeneralServiceAsync(generalServiceId, cancellationToken);
        if (service.CategoryId != request.CategoryId)
        {
            await EnsureCategoryCanReceiveAsync(request.CategoryId, cancellationToken);
        }

        // Checked in the target category, so moving a service where an equivalent name lives is a conflict too.
        await EnsureGeneralServiceNameIsFreeAsync(request.CategoryId, request.Name, generalServiceId, cancellationToken);

        service.Update(request.CategoryId, request.Name, NormalizeDescription(request.Description), request.SortOrder ?? service.SortOrder, UtcNow());
        await _dbContext.SaveAndCommitAsync(transaction, cancellationToken);
        return await MapGeneralServiceAsync(service, cancellationToken);
    }

    public async Task<GeneralServiceView> UpdateGeneralServiceStatusAsync(
        Guid generalServiceId, GeneralServiceStatusUpdateRequest request, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        GeneralService service;
        if (request.IsActive)
        {
            // Reactivating needs an active category: share-lock it first, then the service, and re-check that the
            // service did not move to another category before its own lock was taken.
            service = await LockGeneralServiceUnderItsCategoryAsync(generalServiceId, cancellationToken);
            var categoryIsActive = await _dbContext.ServiceCategories.AsNoTracking()
                .AnyAsync(category => category.Id == service.CategoryId && !category.IsDeleted && category.IsActive, cancellationToken);
            if (!categoryIsActive)
            {
                throw new ConflictException("La categoría del servicio general está inactiva. Actívala o mueve el servicio a otra categoría.");
            }

            service.Activate(UtcNow());
        }
        else
        {
            await _dbContext.LockGeneralServiceAsync(generalServiceId, cancellationToken);
            service = await LoadGeneralServiceAsync(generalServiceId, cancellationToken);
            var activeRates = await _dbContext.Services
                .CountAsync(rate => rate.GeneralServiceId == generalServiceId && !rate.IsDeleted && rate.IsActive, cancellationToken);
            if (activeRates > 0)
            {
                throw new ConflictException(
                    $"El servicio general tiene {activeRates} tarifa(s) activa(s). Desactívalas o asígnalas a otro servicio general antes de desactivarlo.");
            }

            service.Deactivate(UtcNow());
        }

        await _dbContext.SaveAndCommitAsync(transaction, cancellationToken);
        return await MapGeneralServiceAsync(service, cancellationToken);
    }

    public async Task DeleteGeneralServiceAsync(Guid generalServiceId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _dbContext.LockGeneralServiceAsync(generalServiceId, cancellationToken);
        var service = await LoadGeneralServiceAsync(generalServiceId, cancellationToken);
        var rates = await _dbContext.Services
            .CountAsync(rate => rate.GeneralServiceId == generalServiceId && !rate.IsDeleted, cancellationToken);
        if (rates > 0)
        {
            throw new ConflictException(
                $"El servicio general tiene {rates} tarifa(s). Asígnalas a otro servicio general o elimínalas antes de eliminarlo.");
        }

        service.MarkDeleted(UtcNow());

        // BUG-079: the professionals stop offering it. Appointments keep their own copy of the service and duration
        // (ADR-0016 D11), so nothing references an offering. Blocking the deletion while there are offerings or future
        // appointments is a pending business decision; it is not enforced here.
        var offerings = await _dbContext.StaffServiceOfferings
            .Where(offering => offering.GeneralServiceId == generalServiceId)
            .ToListAsync(cancellationToken);
        _dbContext.StaffServiceOfferings.RemoveRange(offerings);

        await _dbContext.SaveAndCommitAsync(transaction, cancellationToken);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private async Task<GeneralService> LockGeneralServiceUnderItsCategoryAsync(Guid generalServiceId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxRelockAttempts; attempt++)
        {
            var categoryId = await _dbContext.GeneralServices.AsNoTracking()
                .Where(service => service.Id == generalServiceId && !service.IsDeleted)
                .Select(service => (Guid?)service.CategoryId)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new KeyNotFoundException("The general service was not found.");

            await _dbContext.ShareCategoryAsync(categoryId, cancellationToken);
            await _dbContext.LockGeneralServiceAsync(generalServiceId, cancellationToken);
            var service = await LoadGeneralServiceAsync(generalServiceId, cancellationToken);
            if (service.CategoryId == categoryId)
            {
                return service;
            }

            // Moved to another category while waiting: start again with the new one.
            _dbContext.Entry(service).State = EntityState.Detached;
        }

        throw new ConflictException("El servicio general cambió mientras se procesaba. Recarga e inténtalo de nuevo.");
    }

    private async Task<ServiceCategory> LoadCategoryAsync(Guid categoryId, CancellationToken cancellationToken)
        => await _dbContext.ServiceCategories
               .SingleOrDefaultAsync(category => category.Id == categoryId && !category.IsDeleted, cancellationToken)
           ?? throw new KeyNotFoundException("The service category was not found.");

    private async Task<GeneralService> LoadGeneralServiceAsync(Guid generalServiceId, CancellationToken cancellationToken)
        => await _dbContext.GeneralServices
               .SingleOrDefaultAsync(service => service.Id == generalServiceId && !service.IsDeleted, cancellationToken)
           ?? throw new KeyNotFoundException("The general service was not found.");

    /// <summary>Read under the caller's lock on the category, so it reflects any committed deactivation or deletion.</summary>
    private async Task EnsureCategoryCanReceiveAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        var category = await _dbContext.ServiceCategories.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == categoryId && !candidate.IsDeleted, cancellationToken);
        if (category is null)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>
            {
                ["categoryId"] = ["La categoría no existe."],
            });
        }

        if (!category.IsActive)
        {
            throw new ConflictException("La categoría está inactiva. Actívala antes de asignarle servicios generales.");
        }
    }

    private async Task EnsureCategoryNameIsFreeAsync(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        var normalized = ServiceCatalogCode.NormalizeName(name);
        if (await _dbContext.ServiceCategories.AnyAsync(
                category => !category.IsDeleted && category.Id != excludeId && category.NormalizedName == normalized, cancellationToken))
        {
            throw new ConflictException(ServiceCatalogLocks.CategoryNameTakenMessage);
        }
    }

    private async Task EnsureGeneralServiceNameIsFreeAsync(Guid categoryId, string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        var normalized = ServiceCatalogCode.NormalizeName(name);
        if (await _dbContext.GeneralServices.AnyAsync(
                service => !service.IsDeleted && service.Id != excludeId && service.CategoryId == categoryId && service.NormalizedName == normalized,
                cancellationToken))
        {
            throw new ConflictException(ServiceCatalogLocks.GeneralServiceNameTakenMessage);
        }
    }

    private static void ValidateCategory(string? name, int sortOrder)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        ValidateName(name, ServiceCategory.NameMinLength, ServiceCategory.NameMaxLength, errors);
        ValidateSortOrder(sortOrder, errors);
        ThrowIfAny(errors);
    }

    private static void ValidateGeneralService(Guid categoryId, string? name, string? description, int sortOrder)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (categoryId == Guid.Empty)
        {
            errors["categoryId"] = ["Elige una categoría."];
        }

        ValidateName(name, GeneralService.NameMinLength, GeneralService.NameMaxLength, errors);
        if (description is not null && description.Trim().Length > GeneralService.DescriptionMaxLength)
        {
            errors["description"] = [$"La descripción admite hasta {GeneralService.DescriptionMaxLength} caracteres."];
        }

        ValidateSortOrder(sortOrder, errors);
        ThrowIfAny(errors);
    }

    private static void ValidateName(string? name, int minLength, int maxLength, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < minLength || name.Trim().Length > maxLength)
        {
            errors["name"] = [$"El nombre debe tener entre {minLength} y {maxLength} caracteres."];
        }
    }

    private static void ValidateSortOrder(int sortOrder, Dictionary<string, string[]> errors)
    {
        if (sortOrder is < 0 or > ServiceCategory.MaxSortOrder)
        {
            errors["sortOrder"] = [$"El orden debe estar entre 0 y {ServiceCategory.MaxSortOrder}."];
        }
    }

    private static void ThrowIfAny(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }
    }

    private static string? NormalizeDescription(string? description)
        => string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    private async Task<Dictionary<Guid, (int Total, int Active)>> CountGeneralServicesAsync(Guid[] categoryIds, CancellationToken cancellationToken)
    {
        var rows = await _dbContext.GeneralServices.AsNoTracking()
            .Where(service => !service.IsDeleted && categoryIds.Contains(service.CategoryId))
            .GroupBy(service => service.CategoryId)
            .Select(group => new { group.Key, Total = group.Count(), Active = group.Count(service => service.IsActive) })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.Key, row => (row.Total, row.Active));
    }

    private async Task<Dictionary<Guid, (int Total, int Active)>> CountRatesAsync(Guid[] generalServiceIds, CancellationToken cancellationToken)
    {
        var rows = await _dbContext.Services.AsNoTracking()
            .Where(rate => !rate.IsDeleted && generalServiceIds.Contains(rate.GeneralServiceId))
            .GroupBy(rate => rate.GeneralServiceId)
            .Select(group => new { group.Key, Total = group.Count(), Active = group.Count(rate => rate.IsActive) })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.Key, row => (row.Total, row.Active));
    }

    private async Task<Dictionary<Guid, ServiceCategory>> LoadCategoryLookupAsync(IEnumerable<Guid> categoryIds, CancellationToken cancellationToken)
    {
        var ids = categoryIds.Distinct().ToArray();
        return await _dbContext.ServiceCategories.AsNoTracking()
            .Where(category => ids.Contains(category.Id))
            .ToDictionaryAsync(category => category.Id, cancellationToken);
    }

    private async Task<ServiceCategoryView> MapCategoryAsync(ServiceCategory category, CancellationToken cancellationToken)
        => MapCategory(category, await CountGeneralServicesAsync([category.Id], cancellationToken));

    private async Task<GeneralServiceView> MapGeneralServiceAsync(GeneralService service, CancellationToken cancellationToken)
        => MapGeneralService(
            service,
            await LoadCategoryLookupAsync([service.CategoryId], cancellationToken),
            await CountRatesAsync([service.Id], cancellationToken));

    private static ServiceCategoryView MapCategory(ServiceCategory category, Dictionary<Guid, (int Total, int Active)> counts)
    {
        var (total, active) = counts.GetValueOrDefault(category.Id);
        return new ServiceCategoryView(
            category.Id, category.Name, category.SortOrder, category.PublicCode, category.IsActive, total, active,
            category.CreatedAt, category.UpdatedAt);
    }

    private static GeneralServiceView MapGeneralService(
        GeneralService service, Dictionary<Guid, ServiceCategory> categories, Dictionary<Guid, (int Total, int Active)> rateCounts)
    {
        var category = categories.GetValueOrDefault(service.CategoryId);
        var (total, active) = rateCounts.GetValueOrDefault(service.Id);
        return new GeneralServiceView(
            service.Id, service.CategoryId, category?.Name ?? string.Empty, category is { IsActive: true, IsDeleted: false },
            service.Name, service.Description, service.SortOrder, service.PublicCode, service.IsActive, total, active,
            service.CreatedAt, service.UpdatedAt);
    }
}
