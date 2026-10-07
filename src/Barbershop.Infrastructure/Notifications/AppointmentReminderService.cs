using System.Globalization;
using Barbershop.Application.Notifications;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Common;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Notifications;

/// <summary>
/// Runs every minute from <see cref="NotificationBackgroundWorker"/>:
/// <list type="bullet">
/// <item>Customers: reminder 24 h and 2 h before their appointment (pending or confirmed).</item>
/// <item>Staff: at the start of each hour, a summary of their appointments in the following hour.</item>
/// </list>
/// Idempotent: each reminder has a dedup key, so running it again never duplicates notifications.
/// </summary>
internal sealed class AppointmentReminderService
{
  internal static readonly TimeSpan DayBeforeLead = TimeSpan.FromHours(24);
  internal static readonly TimeSpan SameDayLead = TimeSpan.FromHours(2);

  /// <summary>The hourly staff summary is only created during the first minutes of each hour.</summary>
  internal const int HourlyAgendaSendWindowMinutes = 20;

  private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("es-CO");

  private readonly AppDbContext _dbContext;
  private readonly INotificationDispatcher _dispatcher;
  private readonly TimeProvider _timeProvider;

  public AppointmentReminderService(AppDbContext dbContext, INotificationDispatcher dispatcher, TimeProvider timeProvider)
  {
    _dbContext = dbContext;
    _dispatcher = dispatcher;
    _timeProvider = timeProvider;
  }

  /// <returns>How many notifications were created.</returns>
  public async Task<int> RunAsync(CancellationToken cancellationToken = default)
  {
    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

    var created = await SendCustomerRemindersAsync(nowUtc, cancellationToken);
    created += await SendStaffHourlyAgendaAsync(nowUtc, cancellationToken);
    return created;
  }

  private async Task<int> SendCustomerRemindersAsync(DateTime nowUtc, CancellationToken cancellationToken)
  {
    var horizonUtc = nowUtc + DayBeforeLead;

    var appointments = await _dbContext.Appointments
        .AsNoTracking()
        .Include(appointment => appointment.StaffProfile)
        .Where(appointment => appointment.CustomerUserId != null
            && (appointment.Status == AppointmentStatus.Pending || appointment.Status == AppointmentStatus.Confirmed)
            && appointment.StartsAt > nowUtc
            && appointment.StartsAt <= horizonUtc)
        .ToListAsync(cancellationToken);

    var candidates = new List<(Appointment Appointment, bool IsSameDay, string DedupKey)>();
    foreach (var appointment in appointments)
    {
      var isSameDay = appointment.StartsAt - nowUtc <= SameDayLead;
      var lead = isSameDay ? SameDayLead : DayBeforeLead;

      // Booked after the reminder moment (e.g. booked this morning for this afternoon):
      // the customer just made the booking, so a reminder right now would be noise.
      if (appointment.CreatedAt > appointment.StartsAt - lead)
      {
        continue;
      }

      // The start time is part of the key: if the appointment is rescheduled, it gets fresh reminders.
      var kind = isSameDay ? "2h" : "24h";
      candidates.Add((appointment, isSameDay, $"reminder-{kind}:{appointment.Id:N}:{appointment.StartsAt:yyyyMMddHHmm}"));
    }

    if (candidates.Count == 0)
    {
      return 0;
    }

    // One query to skip everything already sent (the dispatcher checks again, this just avoids N queries).
    var candidateKeys = candidates
        .Select(candidate => NotificationDispatcher.BuildDedupKey(candidate.DedupKey, candidate.Appointment.CustomerUserId!.Value))
        .ToList();

    var existingKeys = (await _dbContext.UserNotifications
        .Where(notification => notification.DedupKey != null && candidateKeys.Contains(notification.DedupKey))
        .Select(notification => notification.DedupKey!)
        .ToListAsync(cancellationToken))
        .ToHashSet(StringComparer.Ordinal);

    var created = 0;
    foreach (var (appointment, isSameDay, dedupKey) in candidates)
    {
      var customerUserId = appointment.CustomerUserId!.Value;
      if (existingKeys.Contains(NotificationDispatcher.BuildDedupKey(dedupKey, customerUserId)))
      {
        continue;
      }

      created += await _dispatcher.DispatchAsync(
          [customerUserId],
          UserNotificationTypes.AppointmentReminder,
          BuildCustomerReminder(appointment, isSameDay, nowUtc),
          new NotificationDispatchOptions(dedupKey, appointment.StartsAt),
          cancellationToken);
    }

    return created;
  }

  private async Task<int> SendStaffHourlyAgendaAsync(DateTime nowUtc, CancellationToken cancellationToken)
  {
    var localNow = BogotaClock.ToLocal(nowUtc);
    if (localNow.Minute >= HourlyAgendaSendWindowMinutes)
    {
      return 0;
    }

    // At 09:00-09:19 the barber gets the appointments from 10:00 to 10:59.
    var windowStartLocal = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, 0, 0).AddHours(1);
    var windowStartUtc = BogotaClock.ToUtc(DateOnly.FromDateTime(windowStartLocal), TimeOnly.FromDateTime(windowStartLocal));
    var windowEndUtc = windowStartUtc.AddHours(1);

    var appointments = await _dbContext.Appointments
        .AsNoTracking()
        .Include(appointment => appointment.StaffProfile)
        .Where(appointment => (appointment.Status == AppointmentStatus.Pending || appointment.Status == AppointmentStatus.Confirmed)
            && appointment.StartsAt >= windowStartUtc
            && appointment.StartsAt < windowEndUtc
            && appointment.StaffProfile.IsActive)
        .OrderBy(appointment => appointment.StartsAt)
        .ToListAsync(cancellationToken);

    var created = 0;
    foreach (var staffAppointments in appointments.GroupBy(appointment => appointment.StaffProfile.UserId))
    {
      created += await _dispatcher.DispatchAsync(
          [staffAppointments.Key],
          UserNotificationTypes.StaffHourlyAgenda,
          BuildStaffAgenda(staffAppointments.ToList()),
          new NotificationDispatchOptions(
              $"staff-agenda:{windowStartLocal:yyyyMMddHH}",
              windowStartUtc.AddMinutes(30)),
          cancellationToken);
    }

    return created;
  }

  private static PushNotificationMessage BuildCustomerReminder(Appointment appointment, bool isSameDay, DateTime nowUtc)
  {
    var localStart = BogotaClock.ToLocal(appointment.StartsAt);
    var time = FormatTime(localStart);
    var day = DescribeDay(localStart, BogotaClock.ToLocal(nowUtc));
    var staffName = appointment.StaffProfile.DisplayName;

    var title = isSameDay ? $"Tu cita es a las {time}" : "Recordatorio de tu cita";
    var body = appointment.Status == AppointmentStatus.Pending
        ? $"Tu cita con {staffName} {day} a las {time} aún está pendiente de confirmación."
        : isSameDay
            ? $"Te esperamos {day} a las {time} con {staffName}."
            : $"Tienes cita con {staffName} {day} a las {time}.";

    return new PushNotificationMessage(
        title,
        body,
        "/customer/appointments",
        $"appointment-{appointment.Id:N}",
        IsTimeSensitive: true);
  }

  private static PushNotificationMessage BuildStaffAgenda(IReadOnlyList<Appointment> appointments)
  {
    var title = appointments.Count == 1 ? "Próxima hora: 1 cita" : $"Próxima hora: {appointments.Count} citas";

    var body = string.Join(
        " · ",
        appointments.Select(appointment =>
        {
          var pending = appointment.Status == AppointmentStatus.Pending ? " (pendiente)" : string.Empty;
          return $"{FormatTime(BogotaClock.ToLocal(appointment.StartsAt))} {appointment.CustomerName}{pending}";
        }));

    if (body.Length > 1000)
    {
      body = body[..997] + "...";
    }

    return new PushNotificationMessage(title, body, "/staff/appointments", "staff-agenda", IsTimeSensitive: true);
  }

  private static string DescribeDay(DateTime localStart, DateTime localNow)
  {
    if (localStart.Date == localNow.Date)
    {
      return "hoy";
    }

    return localStart.Date == localNow.Date.AddDays(1)
        ? "mañana"
        : $"el {localStart.ToString("dddd d 'de' MMMM", DisplayCulture)}";
  }

  private static string FormatTime(DateTime local) => local.ToString("HH:mm", DisplayCulture);
}
