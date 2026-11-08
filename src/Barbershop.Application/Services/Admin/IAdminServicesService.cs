using Barbershop.Application.Common.Paging;

namespace Barbershop.Application.Services.Admin;

public interface IAdminServicesService
{
    /// <summary>Every non-deleted rate, by name: the options of the selectors (not a history, so not paginated).</summary>
    Task<IReadOnlyList<ServiceView>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>The admin list of rates, paginated by name then id (ADR-0016 B7).</summary>
    Task<PagedResult<ServiceView>> ListAsync(ServiceListQuery query, CancellationToken cancellationToken = default);

    Task<ServiceView> GetByIdAsync(Guid serviceId, CancellationToken cancellationToken = default);

    Task<ServiceView> CreateAsync(ServiceCreateRequest request, CancellationToken cancellationToken = default);

    Task<ServiceView> UpdateAsync(Guid serviceId, ServiceUpdateRequest request, CancellationToken cancellationToken = default);

    Task<ServiceView> UpdateStatusAsync(Guid serviceId, ServiceStatusUpdateRequest request, CancellationToken cancellationToken = default);

    Task SoftDeleteAsync(Guid serviceId, CancellationToken cancellationToken = default);
}
