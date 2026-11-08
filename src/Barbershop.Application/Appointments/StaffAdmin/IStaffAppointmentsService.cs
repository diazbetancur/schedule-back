using Barbershop.Application.Common.Paging;

namespace Barbershop.Application.Appointments;

public interface IStaffAppointmentsService
{
  Task<PagedResult<AppointmentView>> GetForCurrentStaffAsync(Guid currentUserId, StaffAppointmentListQuery query, CancellationToken cancellationToken = default);

  /// <summary>The services the current professional can be booked for right now (their active offerings).</summary>
  Task<IReadOnlyList<AppointmentServiceOption>> GetServiceOptionsForCurrentStaffAsync(Guid currentUserId, CancellationToken cancellationToken = default);

  Task<AppointmentView> CreateForCurrentStaffAsync(Guid currentUserId, StaffManualAppointmentCreateRequest request, CancellationToken cancellationToken = default);

  Task<AppointmentView> UpdateForCurrentStaffAsync(Guid currentUserId, Guid appointmentId, AppointmentUpdateRequest request, CancellationToken cancellationToken = default);

  Task<AppointmentView> UpdateStatusForCurrentStaffAsync(Guid currentUserId, Guid appointmentId, AppointmentStatusUpdateRequest request, CancellationToken cancellationToken = default);
}
