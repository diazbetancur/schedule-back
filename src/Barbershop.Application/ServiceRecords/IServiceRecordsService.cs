namespace Barbershop.Application.ServiceRecords;

/// <summary>A professional registering the services they performed.</summary>
public interface IStaffServiceRecordsService
{
    Task<IReadOnlyList<ServiceRecordCatalogItem>> GetCatalogAsync(CancellationToken cancellationToken = default);

    Task<ServiceRecordEligibilityView> GetEligibilityAsync(Guid currentUserId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServiceRecordAppointmentOption>> GetAppointmentsAsync(Guid currentUserId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServiceRecordView>> GetMineAsync(Guid currentUserId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServiceRecordView>> RegisterAsync(
        Guid currentUserId, ServiceRecordRegisterRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Administrative review of registered services: confirmation (with income or consumption) or rejection.</summary>
public interface IAdminServiceRecordsService
{
    /// <summary>Pending records oldest first; reviewed ones (approved or rejected) newest first.</summary>
    Task<IReadOnlyList<ServiceRecordView>> ListAsync(
        string? status, DateOnly? from, DateOnly? to, Guid? staffProfileId, CancellationToken cancellationToken = default);

    Task<ServiceRecordView> GetByIdAsync(Guid serviceRecordId, CancellationToken cancellationToken = default);

    Task<ServiceRecordOptionsView> GetOptionsAsync(CancellationToken cancellationToken = default);

    Task<ServiceRecordRelatedView> GetRelatedAsync(Guid serviceRecordId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServiceRecordCustomerOption>> SearchCustomersAsync(string? query, CancellationToken cancellationToken = default);

    Task<ServiceRecordView> ApproveAsync(
        Guid currentUserId, Guid serviceRecordId, ServiceRecordApproveRequest request, CancellationToken cancellationToken = default);

    Task<ServiceRecordView> RejectAsync(
        Guid currentUserId, Guid serviceRecordId, ServiceRecordRejectRequest request, CancellationToken cancellationToken = default);
}
