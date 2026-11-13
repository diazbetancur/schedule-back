using Barbershop.Application.Auth;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Common.Paging;
using Barbershop.Application.Services.Admin;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.Services;
using Barbershop.Infrastructure.Loyalty;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Services;

internal sealed class ServiceManagementService : IAdminServicesService
{
    private const int NameMinLength = 2;
    private const int NameMaxLength = 120;

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly ICurrentUserAccessor _currentUser;

    public ServiceManagementService(AppDbContext dbContext, TimeProvider timeProvider, ICurrentUserAccessor currentUser)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<ServiceView>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var services = await _dbContext.Services
            .Where(service => !service.IsDeleted)
            .OrderBy(service => service.Name)
            .ToListAsync(cancellationToken);

        var catalog = await LoadCatalogAsync(services.Select(service => service.GeneralServiceId), cancellationToken);
        return services.Select(service => Map(service, catalog)).ToArray();
    }

    public async Task<PagedResult<ServiceView>> ListAsync(ServiceListQuery query, CancellationToken cancellationToken = default)
    {
        var page = PageRequest.Create(query.Page, query.PageSize);
        var isActive = CatalogStatusFilter.Parse(query.Status);

        var services = _dbContext.Services.AsNoTracking().Where(service => !service.IsDeleted);
        if (ListFilters.Text(query.Q) is { } text)
        {
            var upper = text.ToUpperInvariant();
            services = services.Where(service => service.Name.ToUpper().Contains(upper));
        }

        if (query.GeneralServiceId is { } generalServiceId)
        {
            services = services.Where(service => service.GeneralServiceId == generalServiceId);
        }

        if (query.CategoryId is { } categoryId)
        {
            services = services.Where(service => _dbContext.GeneralServices
                .Any(general => general.Id == service.GeneralServiceId && general.CategoryId == categoryId));
        }

        if (isActive is { } active)
        {
            services = services.Where(service => service.IsActive == active);
        }

        return await services
            .OrderBy(service => service.Name)
            .ThenBy(service => service.Id)
            .ToPagedResultAsync(
                page,
                async (rows, ct) =>
                {
                    var catalog = await LoadCatalogAsync(rows.Select(service => service.GeneralServiceId), ct);
                    return (IReadOnlyList<ServiceView>)rows.Select(service => Map(service, catalog)).ToArray();
                },
                cancellationToken);
    }

    public async Task<ServiceView> GetByIdAsync(Guid serviceId, CancellationToken cancellationToken = default)
    {
        var service = await LoadAsync(serviceId, cancellationToken);
        return await MapAsync(service, cancellationToken);
    }

    public async Task<ServiceView> CreateAsync(ServiceCreateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(
            request.Name, request.BasePrice, request.BusinessPercentage ?? 0, request.LoyaltyPoints, request.GeneralServiceId,
            requireGeneralService: true);
        await EnsureNameIsUniqueAsync(request.Name, null, cancellationToken);

        // Shared lock on the general service, then re-read it: a concurrent deactivation or deletion either committed
        // first (and the rate is refused) or waits and then counts this rate.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _dbContext.ShareGeneralServiceAsync(request.GeneralServiceId!.Value, cancellationToken);
        await EnsureGeneralServiceCanReceiveAsync(request.GeneralServiceId!.Value, cancellationToken);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var service = new Service(request.Name, request.BasePrice, utcNow, request.BusinessPercentage ?? 0, request.GeneralServiceId);
        ApplyLoyaltyPoints(service, request.LoyaltyPoints, utcNow);

        _dbContext.Services.Add(service);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await MapAsync(service, cancellationToken);
    }

    public async Task<ServiceView> UpdateAsync(Guid serviceId, ServiceUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(
            request.Name, request.BasePrice, request.BusinessPercentage ?? 0, request.LoyaltyPoints, request.GeneralServiceId,
            requireGeneralService: false);
        await EnsureNameIsUniqueAsync(request.Name, serviceId, cancellationToken);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        // Global order: general service before rate. The requested one is share-locked even if it does not change.
        if (request.GeneralServiceId is { } requested)
        {
            await _dbContext.ShareGeneralServiceAsync(requested, cancellationToken);
        }

        await _dbContext.LockRateAsync(serviceId, cancellationToken);
        var service = await LoadAsync(serviceId, cancellationToken);
        var moveTo = request.GeneralServiceId is { } target && target != service.GeneralServiceId ? target : (Guid?)null;
        if (moveTo is { } newGeneralService)
        {
            await EnsureGeneralServiceCanReceiveAsync(newGeneralService, cancellationToken);
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        // An omitted percentage keeps the current one (an omitted value used to reset the business share to 0).
        service.Update(request.Name, request.BasePrice, utcNow, request.BusinessPercentage ?? service.BusinessPercentage);
        if (moveTo is { } destination)
        {
            // Only the rate moves: income, records, memberships and redemptions keep the rate id they already have.
            service.AssignGeneralService(destination, utcNow);
        }

        ApplyLoyaltyPoints(service, request.LoyaltyPoints, utcNow);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await MapAsync(service, cancellationToken);
    }

    public async Task<ServiceView> UpdateStatusAsync(Guid serviceId, ServiceStatusUpdateRequest request, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        Service service;
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        if (request.IsActive)
        {
            service = await LockRateUnderItsGeneralServiceAsync(serviceId, cancellationToken);
            var generalServiceIsActive = await _dbContext.GeneralServices.AsNoTracking().AnyAsync(
                general => general.Id == service.GeneralServiceId && !general.IsDeleted && general.IsActive, cancellationToken);
            if (!generalServiceIsActive)
            {
                throw new ConflictException(
                    "El servicio general de esta tarifa está inactivo. Actívalo o asigna la tarifa a otro servicio general.");
            }

            service.Activate(utcNow);
        }
        else
        {
            await _dbContext.LockRateAsync(serviceId, cancellationToken);
            service = await LoadAsync(serviceId, cancellationToken);
            service.Deactivate(utcNow);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await MapAsync(service, cancellationToken);
    }

    /// <summary>Share-locks the rate's general service, then the rate; starts again if the rate moved meanwhile.</summary>
    private async Task<Service> LockRateUnderItsGeneralServiceAsync(Guid serviceId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var generalServiceId = await _dbContext.Services.AsNoTracking()
                .Where(rate => rate.Id == serviceId && !rate.IsDeleted)
                .Select(rate => (Guid?)rate.GeneralServiceId)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new KeyNotFoundException("The service was not found.");

            await _dbContext.ShareGeneralServiceAsync(generalServiceId, cancellationToken);
            await _dbContext.LockRateAsync(serviceId, cancellationToken);
            var service = await LoadAsync(serviceId, cancellationToken);
            if (service.GeneralServiceId == generalServiceId)
            {
                return service;
            }

            _dbContext.Entry(service).State = EntityState.Detached;
        }

        throw new ConflictException("La tarifa cambió mientras se procesaba. Recarga e inténtalo de nuevo.");
    }

    public async Task<ServiceUsageView> GetUsageAsync(Guid serviceId, CancellationToken cancellationToken = default)
    {
        _ = await LoadAsync(serviceId, cancellationToken);

        var plans = await _dbContext.MembershipPlans.AsNoTracking()
            .Where(plan => !plan.IsDeleted && plan.IsActive && plan.Services.Any(included => included.ServiceId == serviceId))
            .OrderBy(plan => plan.Name)
            .Select(plan => plan.Name)
            .ToListAsync(cancellationToken);
        var rewards = await _dbContext.LoyaltyRewards.AsNoTracking()
            .Where(reward => reward.IsActive && reward.ServiceId == serviceId)
            .OrderBy(reward => reward.Name)
            .Select(reward => reward.Name)
            .ToListAsync(cancellationToken);

        return new ServiceUsageView(plans, rewards);
    }

    public async Task SoftDeleteAsync(Guid serviceId, CancellationToken cancellationToken = default)
    {
        var service = await LoadAsync(serviceId, cancellationToken);
        service.MarkDeleted(_timeProvider.GetUtcNow().UtcDateTime);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private void ApplyLoyaltyPoints(Service service, int? requestedPoints, DateTime utcNow)
    {
        var change = LoyaltyCatalogPoints.Change(
            LoyaltyPointsTarget.Service, service.Id, service.LoyaltyPoints, requestedPoints, _currentUser, utcNow);
        if (change is null)
        {
            return;
        }

        service.SetLoyaltyPoints(change.NewPoints, utcNow);
        _dbContext.LoyaltyPointsChanges.Add(change);
    }

    private async Task<Service> LoadAsync(Guid serviceId, CancellationToken cancellationToken)
    {
        return await _dbContext.Services
            .SingleOrDefaultAsync(service => service.Id == serviceId && !service.IsDeleted, cancellationToken)
            ?? throw new KeyNotFoundException("The service was not found.");
    }

    private async Task EnsureNameIsUniqueAsync(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        var normalized = name.Trim().ToUpperInvariant();
        var duplicateExists = await _dbContext.Services.AnyAsync(
            service => !service.IsDeleted
                && service.Id != excludeId
                && service.Name.ToUpper() == normalized,
            cancellationToken);

        if (duplicateExists)
        {
            throw new ConflictException("A service with this name already exists.");
        }
    }

    /// <summary>Read under the caller's shared lock on the general service, so it reflects committed changes.</summary>
    private async Task EnsureGeneralServiceCanReceiveAsync(Guid generalServiceId, CancellationToken cancellationToken)
    {
        var generalService = await _dbContext.GeneralServices.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == generalServiceId && !candidate.IsDeleted, cancellationToken);
        if (generalService is null)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>
            {
                ["generalServiceId"] = ["El servicio general no existe."],
            });
        }

        if (!generalService.IsActive)
        {
            throw new ConflictException("El servicio general está inactivo. Actívalo antes de asignarle tarifas.");
        }
    }

    private static void ValidateRequest(
        string name, int basePrice, int businessPercentage, int? loyaltyPoints, Guid? generalServiceId, bool requireGeneralService)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (generalServiceId == Guid.Empty || (requireGeneralService && generalServiceId is null))
        {
            errors["generalServiceId"] = ["Elige el servicio general de la tarifa."];
        }

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length is < NameMinLength or > NameMaxLength)
        {
            errors["name"] = [$"Name must be between {NameMinLength} and {NameMaxLength} characters."];
        }

        if (basePrice < 0)
        {
            errors["basePrice"] = ["BasePrice must be zero or greater."];
        }

        if (businessPercentage is < 0 or > 100)
        {
            errors["businessPercentage"] = ["BusinessPercentage must be between 0 and 100."];
        }

        LoyaltyCatalogPoints.Validate(loyaltyPoints, errors);

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }
    }

    private sealed record CatalogEntry(string GeneralServiceName, bool GeneralServiceIsActive, Guid CategoryId, string CategoryName);

    private async Task<Dictionary<Guid, CatalogEntry>> LoadCatalogAsync(IEnumerable<Guid> generalServiceIds, CancellationToken cancellationToken)
    {
        var ids = generalServiceIds.Distinct().ToArray();
        var rows = await (
                from general in _dbContext.GeneralServices.AsNoTracking()
                join category in _dbContext.ServiceCategories.AsNoTracking() on general.CategoryId equals category.Id
                where ids.Contains(general.Id)
                select new { general.Id, general.Name, general.IsActive, general.IsDeleted, CategoryId = category.Id, CategoryName = category.Name })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(
            row => row.Id,
            row => new CatalogEntry(row.Name, row.IsActive && !row.IsDeleted, row.CategoryId, row.CategoryName));
    }

    private async Task<ServiceView> MapAsync(Service service, CancellationToken cancellationToken)
        => Map(service, await LoadCatalogAsync([service.GeneralServiceId], cancellationToken));

    private static ServiceView Map(Service service, Dictionary<Guid, CatalogEntry> catalog)
    {
        var entry = catalog.GetValueOrDefault(service.GeneralServiceId);
        return new ServiceView(
            service.Id, service.Name, service.BasePrice, service.BusinessPercentage, service.IsActive,
            service.CreatedAt, service.UpdatedAt, service.LoyaltyPoints,
            service.GeneralServiceId, entry?.GeneralServiceName ?? string.Empty, entry?.GeneralServiceIsActive ?? false,
            entry?.CategoryId ?? Guid.Empty, entry?.CategoryName ?? string.Empty);
    }
}
