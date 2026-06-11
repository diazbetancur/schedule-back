namespace Barbershop.Application.Notifications;

public sealed record AppointmentNotificationContext(
    Guid StaffUserId,
    string StaffDisplayName,
    Guid? CustomerUserId,
    string CustomerName,
    DateTime StartsAtUtc);

public interface IAppointmentNotificationService
{
  Task NotifyStaffOfNewAppointmentAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default);

  Task NotifyStaffOfCustomerCancellationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default);

  Task NotifyCustomerOfAppointmentUpdateAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default);

  Task NotifyCustomerOfAppointmentCancellationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default);
}
