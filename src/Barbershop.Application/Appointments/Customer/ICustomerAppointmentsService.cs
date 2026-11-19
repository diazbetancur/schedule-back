using Barbershop.Application.Common.Paging;

namespace Barbershop.Application.Appointments;

public interface ICustomerAppointmentsService
{
  Task<AppointmentView> CreateAsync(Guid currentUserId, CustomerAppointmentCreateRequest request, CancellationToken cancellationToken = default);

  Task<PagedResult<AppointmentView>> GetHistoryAsync(Guid currentUserId, CustomerAppointmentListQuery query, CancellationToken cancellationToken = default);

  /// <summary>SONDA BUG-118: one appointment of the signed-in customer, whatever its date or status; 404 if it is not theirs.</summary>
  Task<AppointmentView> GetByIdAsync(Guid currentUserId, Guid appointmentId, CancellationToken cancellationToken = default);

  Task<AppointmentView> CancelAsync(Guid currentUserId, Guid appointmentId, CancellationToken cancellationToken = default);

  Task<AppointmentView> RescheduleAsync(Guid currentUserId, Guid appointmentId, CustomerAppointmentRescheduleRequest request, CancellationToken cancellationToken = default);
}
