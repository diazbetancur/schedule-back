namespace Barbershop.Application.Notifications;

public static class UserNotificationTypes
{
  public const string AppointmentCreated = "appointment.created";
  public const string AppointmentCancelledByCustomer = "appointment.cancelled_by_customer";
  public const string AppointmentUpdated = "appointment.updated";
  public const string AppointmentCancelled = "appointment.cancelled";
  public const string AppointmentConfirmed = "appointment.confirmed";
  public const string Campaign = "campaign";
}

public sealed record UserNotificationView(
    Guid Id,
    string Type,
    string Title,
    string Body,
    string? Url,
    DateTime CreatedAtUtc,
    DateTime? ReadAtUtc);

public sealed record UserNotificationInboxView(int UnreadCount, IReadOnlyList<UserNotificationView> Items);
