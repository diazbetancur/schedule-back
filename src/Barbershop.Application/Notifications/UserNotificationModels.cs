namespace Barbershop.Application.Notifications;

public static class UserNotificationTypes
{
  public const string AppointmentCreated = "appointment.created";
  public const string AppointmentCancelledByCustomer = "appointment.cancelled_by_customer";
  public const string AppointmentUpdated = "appointment.updated";
  public const string AppointmentCancelled = "appointment.cancelled";
  public const string AppointmentConfirmed = "appointment.confirmed";
  public const string Campaign = "campaign";
  public const string AppointmentReminder = "appointment.reminder";
  public const string StaffHourlyAgenda = "staff.hourly_agenda";
}

public sealed record UserNotificationView(
    Guid Id,
    string Type,
    string Title,
    string Body,
    string? Url,
    DateTime CreatedAtUtc,
    DateTime? ReadAtUtc);

/// <param name="HasMore">BUG-107: more notifications after this page (the panel offers «Ver más»).</param>
public sealed record UserNotificationInboxView(int UnreadCount, IReadOnlyList<UserNotificationView> Items, bool HasMore = false);
