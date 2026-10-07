using System.Globalization;
using Barbershop.Application.Notifications;
using Barbershop.Domain.Common;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Notifications;

internal sealed class AppointmentNotificationService : IAppointmentNotificationService
{
  private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("es-CO");
  private static readonly string NormalizedAdminRole = RoleNames.Admin.ToUpperInvariant();

  private readonly INotificationDispatcher _dispatcher;
  private readonly AppDbContext _dbContext;

  public AppointmentNotificationService(INotificationDispatcher dispatcher, AppDbContext dbContext)
  {
    _dispatcher = dispatcher;
    _dbContext = dbContext;
  }

  public async Task NotifyStaffOfNewAppointmentAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default)
  {
    var message = new PushNotificationMessage(
        "Nueva cita agendada",
        $"{context.CustomerName} agendó una cita para el {FormatDateTime(context.StartsAtUtc)}.",
        "/staff/appointments",
        AppointmentTag(context));

    var recipients = await ResolveStaffAndAdminRecipientsAsync(context.StaffUserId, cancellationToken);
    await _dispatcher.DispatchAsync(recipients, UserNotificationTypes.AppointmentCreated, message, cancellationToken);
  }

  public async Task NotifyStaffOfCustomerCancellationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default)
  {
    var message = new PushNotificationMessage(
        "Cita cancelada",
        $"{context.CustomerName} canceló su cita del {FormatDateTime(context.StartsAtUtc)}.",
        "/staff/appointments",
        AppointmentTag(context));

    var recipients = await ResolveStaffAndAdminRecipientsAsync(context.StaffUserId, cancellationToken);
    await _dispatcher.DispatchAsync(recipients, UserNotificationTypes.AppointmentCancelledByCustomer, message, cancellationToken);
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
        "/customer/appointments",
        AppointmentTag(context));

    return _dispatcher.DispatchAsync([customerUserId], UserNotificationTypes.AppointmentUpdated, message, cancellationToken);
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
        "/customer/appointments",
        AppointmentTag(context));

    return _dispatcher.DispatchAsync([customerUserId], UserNotificationTypes.AppointmentCancelled, message, cancellationToken);
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
        "/customer/appointments",
        AppointmentTag(context));

    return _dispatcher.DispatchAsync([customerUserId], UserNotificationTypes.AppointmentConfirmed, message, cancellationToken);
  }

  private async Task<IReadOnlyCollection<Guid>> ResolveStaffAndAdminRecipientsAsync(Guid staffUserId, CancellationToken cancellationToken)
  {
    var adminUserIds = await _dbContext.Users
        .Where(user => user.IsActive && user.UserRoles.Any(userRole => userRole.Role.NormalizedName == NormalizedAdminRole))
        .Select(user => user.Id)
        .ToListAsync(cancellationToken);

    return adminUserIds.Append(staffUserId).Distinct().ToArray();
  }

  // Same tag per appointment: a later update replaces the earlier notification on the device.
  private static string? AppointmentTag(AppointmentNotificationContext context)
      => context.AppointmentId is { } appointmentId ? $"appointment-{appointmentId:N}" : null;

  private static string FormatDateTime(DateTime startsAtUtc)
  {
    var local = BogotaClock.ToLocal(startsAtUtc);
    return local.ToString("dddd d 'de' MMMM 'a las' HH:mm", DisplayCulture);
  }
}
