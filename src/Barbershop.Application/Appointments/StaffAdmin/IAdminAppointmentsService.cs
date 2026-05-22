namespace Barbershop.Application.Appointments;

public interface IAdminAppointmentsService
{
  Task<IReadOnlyList<AppointmentView>> GetAsync(Guid? staffProfileId, CancellationToken cancellationToken = default);

  Task<AppointmentView> CreateAsync(AdminManualAppointmentCreateRequest request, CancellationToken cancellationToken = default);

  Task<AppointmentView> UpdateAsync(Guid appointmentId, AppointmentUpdateRequest request, CancellationToken cancellationToken = default);

  Task<AppointmentView> UpdateStatusAsync(Guid appointmentId, AppointmentStatusUpdateRequest request, CancellationToken cancellationToken = default);
}
