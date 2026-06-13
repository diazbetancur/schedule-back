using System.Globalization;
using Barbershop.Application.Notifications;

namespace Barbershop.Infrastructure.Notifications;

internal sealed class AppointmentNotificationService : IAppointmentNotificationService
{
  private static readonly TimeSpan DisplayUtcOffset = TimeSpan.FromHours(-5);
  private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("es-CO");

  private readonly IPushNotificationSender _sender;

  public AppointmentNotificationService(IPushNotificationSender sender)
  {
    _sender = sender;
  }

  public Task NotifyStaffOfNewAppointmentAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default)
  {
    var message = new PushNotificationMessage(
        "Nueva cita agendada",
        $"{context.CustomerName} agendó una cita para el {FormatDateTime(context.StartsAtUtc)}.",
        "/staff/appointments");

    return _sender.SendToUsersAsync([context.StaffUserId], message, cancellationToken);
  }

  public Task NotifyStaffOfCustomerCancellationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default)
  {
    var message = new PushNotificationMessage(
        "Cita cancelada",
        $"{context.CustomerName} canceló su cita del {FormatDateTime(context.StartsAtUtc)}.",
        "/staff/appointments");

    return _sender.SendToUsersAsync([context.StaffUserId], message, cancellationToken);
  }

  public Task NotifyCustomerOfAppointmentUpdateAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default)
  {
    if (context.CustomerUserId is not { } customerUserId)
    {
      return Task.CompletedTask;
    }

    var message = new PushNotificationMessage(
        "Tu cita fue modificada",
        $"{context.StaffDisplayName} modificó tu cita. Nueva fecha: {FormatDateTime(context.StartsAtUtc)}.",
        "/customer/appointments");

    return _sender.SendToUsersAsync([customerUserId], message, cancellationToken);
  }

  public Task NotifyCustomerOfAppointmentCancellationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default)
  {
    if (context.CustomerUserId is not { } customerUserId)
    {
      return Task.CompletedTask;
    }

    var message = new PushNotificationMessage(
        "Tu cita fue cancelada",
        $"{context.StaffDisplayName} canceló tu cita del {FormatDateTime(context.StartsAtUtc)}.",
        "/customer/appointments");

    return _sender.SendToUsersAsync([customerUserId], message, cancellationToken);
  }

  public Task NotifyCustomerOfAppointmentConfirmationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default)
  {
    if (context.CustomerUserId is not { } customerUserId)
    {
      return Task.CompletedTask;
    }

    var message = new PushNotificationMessage(
        "Tu cita fue confirmada",
        $"{context.StaffDisplayName} confirmó tu cita del {FormatDateTime(context.StartsAtUtc)}.",
        "/customer/appointments");

    return _sender.SendToUsersAsync([customerUserId], message, cancellationToken);
  }

  private static string FormatDateTime(DateTime startsAtUtc)
  {
    var local = DateTime.SpecifyKind(startsAtUtc.Add(DisplayUtcOffset), DateTimeKind.Unspecified);
    return local.ToString("dddd d 'de' MMMM 'a las' HH:mm", DisplayCulture);
  }
}
