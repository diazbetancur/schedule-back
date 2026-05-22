namespace Barbershop.Application.Appointments;

public interface IStaffAppointmentsService
{
  Task<IReadOnlyList<AppointmentView>> GetForCurrentStaffAsync(Guid currentUserId, CancellationToken cancellationToken = default);

  Task<AppointmentView> CreateForCurrentStaffAsync(Guid currentUserId, StaffManualAppointmentCreateRequest request, CancellationToken cancellationToken = default);

  Task<AppointmentView> UpdateForCurrentStaffAsync(Guid currentUserId, Guid appointmentId, AppointmentUpdateRequest request, CancellationToken cancellationToken = default);

  Task<AppointmentView> UpdateStatusForCurrentStaffAsync(Guid currentUserId, Guid appointmentId, AppointmentStatusUpdateRequest request, CancellationToken cancellationToken = default);
}
