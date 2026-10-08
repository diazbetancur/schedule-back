using System.Globalization;
using Barbershop.Application.Auth;
using Barbershop.Application.Notifications;
using Barbershop.Domain.Common;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Notifications;

internal sealed class AppointmentNotificationService : IAppointmentNotificationService
{
  internal const string StaffAppointmentsUrl = "/staff/appointments";
  internal const string AdminAppointmentsUrl = "/admin/appointments";
  internal const string CustomerAppointmentsUrl = "/customer/appointments";

  private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("es-CO");
  private static readonly string NormalizedAdminRole = RoleNames.Admin.ToUpperInvariant();

  private readonly INotificationDispatcher _dispatcher;
  private readonly AppDbContext _dbContext;
  private readonly ICurrentUserAccessor _currentUser;

  public AppointmentNotificationService(INotificationDispatcher dispatcher, AppDbContext dbContext, ICurrentUserAccessor currentUser)
  {
    _dispatcher = dispatcher;
    _dbContext = dbContext;
    _currentUser = currentUser;
  }

  public Task NotifyStaffOfNewAppointmentAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default)
  {
    var when = FormatDateTime(context.StartsAtUtc);
    var bookedByCustomer = context.CustomerUserId is not null;

    return NotifyStaffSideAsync(
        context,
        UserNotificationTypes.AppointmentCreated,
        "Nueva cita agendada",
        staffBody: bookedByCustomer
            ? $"{context.CustomerName} agendó una cita para el {when}."
            : $"Se agendó una cita de {context.CustomerName} para el {when}.",
        adminBody: bookedByCustomer
            ? $"{context.CustomerName} agendó una cita con {context.StaffDisplayName} para el {when}."
            : $"Se agendó una cita de {context.CustomerName} con {context.StaffDisplayName} para el {when}.",
        cancellationToken);
  }

  public Task NotifyStaffOfCustomerCancellationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default)
  {
    var when = FormatDateTime(context.StartsAtUtc);

    return NotifyStaffSideAsync(
        context,
        UserNotificationTypes.AppointmentCancelledByCustomer,
        "Cita cancelada",
        staffBody: $"{context.CustomerName} canceló su cita del {when}.",
        adminBody: $"{context.CustomerName} canceló su cita con {context.StaffDisplayName} del {when}.",
        cancellationToken);
  }

  public Task NotifyStaffOfAppointmentRescheduledAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default)
  {
    var when = FormatDateTime(context.StartsAtUtc);

    return NotifyStaffSideAsync(
        context,
        UserNotificationTypes.AppointmentUpdated,
        "Cita reprogramada",
        staffBody: $"La cita de {context.CustomerName} ahora es el {when}.",
        adminBody: $"La cita de {context.CustomerName} con {context.StaffDisplayName} ahora es el {when}.",
        cancellationToken);
  }

  public Task NotifyStaffOfAppointmentCancelledAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default)
  {
    var when = FormatDateTime(context.StartsAtUtc);

    return NotifyStaffSideAsync(
        context,
        UserNotificationTypes.AppointmentCancelled,
        "Cita cancelada",
        staffBody: $"Se canceló la cita de {context.CustomerName} del {when}.",
        adminBody: $"Se canceló la cita de {context.CustomerName} con {context.StaffDisplayName} del {when}.",
        cancellationToken);
  }

  public Task NotifyCustomerOfAppointmentUpdateAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default)
      => NotifyCustomerAsync(
          context,
          UserNotificationTypes.AppointmentUpdated,
          "Tu cita fue modificada",
          $"{CustomerFacingActor(context)} modificó tu cita. Nueva fecha: {FormatDateTime(context.StartsAtUtc)}.",
          cancellationToken);

  public Task NotifyCustomerOfAppointmentCancellationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default)
      => NotifyCustomerAsync(
          context,
          UserNotificationTypes.AppointmentCancelled,
          "Tu cita fue cancelada",
          $"{CustomerFacingActor(context)} canceló tu cita del {FormatDateTime(context.StartsAtUtc)}.",
          cancellationToken);

  public Task NotifyCustomerOfAppointmentConfirmationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default)
      => NotifyCustomerAsync(
          context,
          UserNotificationTypes.AppointmentConfirmed,
          "Tu cita fue confirmada",
          $"{CustomerFacingActor(context)} confirmó tu cita del {FormatDateTime(context.StartsAtUtc)}.",
          cancellationToken);

  /// <summary>
  /// The barber gets a link to their agenda; admins get a link to the admin agenda and the barber's name
  /// in the text. Whoever performed the action is skipped.
  /// </summary>
  private async Task NotifyStaffSideAsync(
      AppointmentNotificationContext context,
      string type,
      string title,
      string staffBody,
      string adminBody,
      CancellationToken cancellationToken)
  {
    var actorUserId = _currentUser.UserId;
    var tag = AppointmentTag(context);

    if (context.StaffUserId != actorUserId)
    {
      await _dispatcher.DispatchAsync(
          [context.StaffUserId],
          type,
          new PushNotificationMessage(title, staffBody, StaffAppointmentsUrl, tag),
          cancellationToken);
    }

    // A barber who is also admin already got the barber notification above.
    var adminUserIds = await _dbContext.Users
        .AsNoTracking()
        .Where(user => user.IsActive
            && user.Id != context.StaffUserId
            && user.UserRoles.Any(userRole => userRole.Role.NormalizedName == NormalizedAdminRole))
        .Select(user => user.Id)
        .ToListAsync(cancellationToken);

    if (actorUserId is { } actorId)
    {
      adminUserIds.Remove(actorId);
    }

    if (adminUserIds.Count == 0)
    {
      return;
    }

    await _dispatcher.DispatchAsync(
        adminUserIds,
        type,
        new PushNotificationMessage(title, adminBody, AdminAppointmentsUrl, tag),
        cancellationToken);
  }

  private Task NotifyCustomerAsync(
      AppointmentNotificationContext context,
      string type,
      string title,
      string body,
      CancellationToken cancellationToken)
  {
    if (context.CustomerUserId is not { } customerUserId)
    {
      return Task.CompletedTask;
    }

    return _dispatcher.DispatchAsync(
        [customerUserId],
        type,
        new PushNotificationMessage(title, body, CustomerAppointmentsUrl, AppointmentTag(context)),
        cancellationToken);
  }

  /// <summary>If an admin (not the barber) made the change, the customer sees "La barbería" instead of the barber's name.</summary>
  private string CustomerFacingActor(AppointmentNotificationContext context)
      => _currentUser.UserId is { } actorId && actorId != context.StaffUserId
          ? "La barbería"
          : context.StaffDisplayName;

  // Same tag per appointment: a later update replaces the earlier notification on the device.
  private static string? AppointmentTag(AppointmentNotificationContext context)
      => context.AppointmentId is { } appointmentId ? $"appointment-{appointmentId:N}" : null;

  private static string FormatDateTime(DateTime startsAtUtc)
  {
    var local = BogotaClock.ToLocal(startsAtUtc);
    return local.ToString("dddd d 'de' MMMM 'a las' HH:mm", DisplayCulture);
  }
}
