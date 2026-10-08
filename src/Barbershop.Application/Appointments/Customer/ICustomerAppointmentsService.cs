namespace Barbershop.Application.Appointments;

public interface ICustomerAppointmentsService
{
  Task<AppointmentView> CreateAsync(Guid currentUserId, CustomerAppointmentCreateRequest request, CancellationToken cancellationToken = default);

  Task<IReadOnlyList<AppointmentView>> GetHistoryAsync(Guid currentUserId, CancellationToken cancellationToken = default);

  Task<AppointmentView> CancelAsync(Guid currentUserId, Guid appointmentId, CancellationToken cancellationToken = default);

  Task<AppointmentView> RescheduleAsync(Guid currentUserId, Guid appointmentId, CustomerAppointmentRescheduleRequest request, CancellationToken cancellationToken = default);
}
