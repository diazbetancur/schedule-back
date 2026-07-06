namespace Barbershop.Application.Services.Admin;

public interface IAdminServicesService
{
    Task<IReadOnlyList<ServiceView>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<ServiceView> GetByIdAsync(Guid serviceId, CancellationToken cancellationToken = default);

    Task<ServiceView> CreateAsync(ServiceCreateRequest request, CancellationToken cancellationToken = default);

    Task<ServiceView> UpdateAsync(Guid serviceId, ServiceUpdateRequest request, CancellationToken cancellationToken = default);

    Task<ServiceView> UpdateStatusAsync(Guid serviceId, ServiceStatusUpdateRequest request, CancellationToken cancellationToken = default);

    Task SoftDeleteAsync(Guid serviceId, CancellationToken cancellationToken = default);
}
