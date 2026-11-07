using Barbershop.Application.Common.Paging;

namespace Barbershop.Application.ServiceRecords;

/// <summary>A professional registering the services they performed.</summary>
public interface IStaffServiceRecordsService
{
    Task<IReadOnlyList<ServiceRecordCatalogItem>> GetCatalogAsync(CancellationToken cancellationToken = default);

    Task<ServiceRecordEligibilityView> GetEligibilityAsync(Guid currentUserId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServiceRecordAppointmentOption>> GetAppointmentsAsync(Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The professional's own records, paginated: pending ones first (oldest registration first, so none is hidden
    /// however old it is), then the reviewed ones, newest first.
    /// </summary>
    Task<PagedResult<ServiceRecordView>> GetMineAsync(
        Guid currentUserId, PageQuery? query = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServiceRecordView>> RegisterAsync(
        Guid currentUserId, ServiceRecordRegisterRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Administrative review of registered services: confirmation (with income or consumption) or rejection.</summary>
public interface IAdminServiceRecordsService
{
    /// <summary>
    /// Paginated (ADR-0016 B7). Pending records oldest first (a review queue); reviewed ones (approved or rejected)
    /// newest first. Ties are broken by registration position and id.
    /// </summary>
    Task<PagedResult<ServiceRecordView>> ListAsync(ServiceRecordListQuery query, CancellationToken cancellationToken = default);

    Task<ServiceRecordView> GetByIdAsync(Guid serviceRecordId, CancellationToken cancellationToken = default);

    Task<ServiceRecordOptionsView> GetOptionsAsync(CancellationToken cancellationToken = default);

    Task<ServiceRecordRelatedView> GetRelatedAsync(Guid serviceRecordId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServiceRecordCustomerOption>> SearchCustomersAsync(string? query, CancellationToken cancellationToken = default);

    /// <summary>Approved service redemptions of the customer (optionally for one service) waiting for delivery.</summary>
    Task<IReadOnlyList<ServiceRecordRedemptionOption>> GetRedemptionOptionsAsync(
        Guid customerId, Guid? serviceId, CancellationToken cancellationToken = default);

    Task<ServiceRecordView> ApproveAsync(
        Guid currentUserId, Guid serviceRecordId, ServiceRecordApproveRequest request, CancellationToken cancellationToken = default);

    Task<ServiceRecordView> RejectAsync(
        Guid currentUserId, Guid serviceRecordId, ServiceRecordRejectRequest request, CancellationToken cancellationToken = default);
}
