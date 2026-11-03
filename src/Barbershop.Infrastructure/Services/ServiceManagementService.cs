using Barbershop.Application.Auth;
using Barbershop.Application.Common.Exceptions;
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

        return services.Select(Map).ToArray();
    }

    public async Task<ServiceView> GetByIdAsync(Guid serviceId, CancellationToken cancellationToken = default)
    {
        var service = await LoadAsync(serviceId, cancellationToken);
        return Map(service);
    }

    public async Task<ServiceView> CreateAsync(ServiceCreateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request.Name, request.BasePrice, request.BusinessPercentage, request.LoyaltyPoints);
        await EnsureNameIsUniqueAsync(request.Name, null, cancellationToken);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var service = new Service(request.Name, request.BasePrice, utcNow, request.BusinessPercentage);
        ApplyLoyaltyPoints(service, request.LoyaltyPoints, utcNow);

        _dbContext.Services.Add(service);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Map(service);
    }

    public async Task<ServiceView> UpdateAsync(Guid serviceId, ServiceUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request.Name, request.BasePrice, request.BusinessPercentage, request.LoyaltyPoints);
        var service = await LoadAsync(serviceId, cancellationToken);
        await EnsureNameIsUniqueAsync(request.Name, serviceId, cancellationToken);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        service.Update(request.Name, request.BasePrice, utcNow, request.BusinessPercentage);
        ApplyLoyaltyPoints(service, request.LoyaltyPoints, utcNow);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Map(service);
    }

    public async Task<ServiceView> UpdateStatusAsync(Guid serviceId, ServiceStatusUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var service = await LoadAsync(serviceId, cancellationToken);
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        if (request.IsActive)
        {
            service.Activate(utcNow);
        }
        else
        {
            service.Deactivate(utcNow);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Map(service);
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

    private static void ValidateRequest(string name, int basePrice, int businessPercentage, int? loyaltyPoints)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

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

    private static ServiceView Map(Service service)
        => new(
            service.Id, service.Name, service.BasePrice, service.BusinessPercentage, service.IsActive,
            service.CreatedAt, service.UpdatedAt, service.LoyaltyPoints);
}
