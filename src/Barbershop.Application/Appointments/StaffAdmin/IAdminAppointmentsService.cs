using Barbershop.Application.Common.Paging;

namespace Barbershop.Application.Appointments;

public interface IAdminAppointmentsService
{
  Task<PagedResult<AppointmentView>> GetAsync(AdminAppointmentListQuery query, CancellationToken cancellationToken = default);

  /// <summary>The services the professional can be booked for right now (active offerings), for the manual form.</summary>
  Task<IReadOnlyList<AppointmentServiceOption>> GetServiceOptionsAsync(Guid staffProfileId, CancellationToken cancellationToken = default);

  Task<AppointmentView> CreateAsync(AdminManualAppointmentCreateRequest request, CancellationToken cancellationToken = default);

  Task<AppointmentView> UpdateAsync(Guid appointmentId, AppointmentUpdateRequest request, CancellationToken cancellationToken = default);

  Task<AppointmentView> UpdateStatusAsync(Guid appointmentId, AppointmentStatusUpdateRequest request, CancellationToken cancellationToken = default);
}
