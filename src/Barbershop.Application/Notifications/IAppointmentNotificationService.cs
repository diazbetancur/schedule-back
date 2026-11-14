namespace Barbershop.Application.Notifications;

public sealed record AppointmentNotificationContext(
    Guid StaffUserId,
    string StaffDisplayName,
    Guid? CustomerUserId,
    string CustomerName,
    DateTime StartsAtUtc,
    Guid? AppointmentId = null);

/// <summary>
/// "Staff side" notifications go to the barber of the appointment and to every active admin,
/// except whoever performed the action (nobody gets notified about what they just did).
/// </summary>
public interface IAppointmentNotificationService
{
  Task NotifyStaffOfNewAppointmentAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default);

  Task NotifyStaffOfCustomerCancellationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default);

  /// <summary>The barber or an admin moved the appointment to another date/time.</summary>
  Task NotifyStaffOfAppointmentRescheduledAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default);

  /// <summary>The barber or an admin cancelled the appointment.</summary>
  Task NotifyStaffOfAppointmentCancelledAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default);

  /// <summary>The date or time of the appointment changed (the message states the new one).</summary>
  Task NotifyCustomerOfAppointmentUpdateAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default);

  /// <summary>
  /// BUG-115: the date stayed but the service or the duration changed; the message never claims a new date. Edits that
  /// only touch notes or contact data notify nobody.
  /// </summary>
  Task NotifyCustomerOfAppointmentDetailsChangeAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

  Task NotifyCustomerOfAppointmentCancellationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default);

  Task NotifyCustomerOfAppointmentConfirmationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default);
}
