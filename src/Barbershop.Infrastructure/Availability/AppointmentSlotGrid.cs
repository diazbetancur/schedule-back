using Barbershop.Application.Availability;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Common;
using Barbershop.Domain.Scheduling;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Availability;

/// <summary>
/// Where an appointment booked by a customer may start (ADR-0022, replaces ADR-0016 F1). The one place that computes
/// it: the public availability lists these slots, and booking and rescheduling accept only a start among them.
/// <list type="bullet">
/// <item>P1/P2: from the start of each availability rule (Bogotá time), one start every <c>duration</c>, as long as
/// the whole service fits inside the rule (a rule never crosses midnight, so the slot is in one Bogotá day).</item>
/// <item>P3: a candidate that overlaps an occupied time (an absence or a pending/confirmed appointment) moves to the
/// end of that occupied time, not to the next mark of the grid, so no loose gaps are left.</item>
/// <item>P4: starts before now are left out without moving the anchor.</item>
/// </list>
/// </summary>
internal static class AppointmentSlotGrid
{
  /// <summary>
  /// Free slots of <paramref name="staffProfileId"/> lasting <paramref name="duration"/> on the Bogotá days
  /// <paramref name="from"/>..<paramref name="to"/>, starting at or after <paramref name="utcNow"/>, in order.
  /// <paramref name="excludedAppointmentId"/> (the appointment being rescheduled) does not count as occupied.
  /// </summary>
  public static async Task<IReadOnlyList<PublicAvailabilitySlotResponse>> FindAsync(
      AppDbContext dbContext,
      Guid staffProfileId,
      DateOnly from,
      DateOnly to,
      TimeSpan duration,
      Guid? excludedAppointmentId,
      DateTime utcNow,
      CancellationToken cancellationToken)
  {
    if (duration <= TimeSpan.Zero)
    {
      return [];
    }

    var rangeStart = BogotaClock.ToUtc(from, TimeOnly.MinValue);
    var rangeEndExclusive = BogotaClock.ToUtc(to.AddDays(1), TimeOnly.MinValue);

    var rules = await dbContext.StaffAvailabilityRules
        .AsNoTracking()
        .Where(rule => rule.StaffProfileId == staffProfileId && rule.IsActive)
        .ToListAsync(cancellationToken);

    var absences = await dbContext.StaffUnavailablePeriods
        .AsNoTracking()
        .Where(period => period.StaffProfileId == staffProfileId && period.EndsAt > rangeStart && period.StartsAt < rangeEndExclusive)
        .Select(period => new { period.StartsAt, period.EndsAt })
        .ToListAsync(cancellationToken);

    var appointments = await dbContext.Appointments
        .AsNoTracking()
        .Where(appointment => appointment.StaffProfileId == staffProfileId
            && (appointment.Status == AppointmentStatus.Pending || appointment.Status == AppointmentStatus.Confirmed)
            && appointment.EndsAt > rangeStart
            && appointment.StartsAt < rangeEndExclusive
            && (excludedAppointmentId == null || appointment.Id != excludedAppointmentId))
        .Select(appointment => new { appointment.StartsAt, appointment.EndsAt })
        .ToListAsync(cancellationToken);

    var occupied = absences.Select(item => (item.StartsAt, item.EndsAt))
        .Concat(appointments.Select(item => (item.StartsAt, item.EndsAt)))
        .ToArray();

    var rulesByDay = rules
        .GroupBy(rule => rule.DayOfWeek)
        .ToDictionary(group => group.Key, group => group.OrderBy(rule => rule.StartTime).ToArray());

    var slots = new SortedDictionary<DateTime, PublicAvailabilitySlotResponse>();
    for (var date = from; date <= to; date = date.AddDays(1))
    {
      if (!rulesByDay.TryGetValue(date.DayOfWeek, out var dayRules))
      {
        continue;
      }

      foreach (var rule in dayRules)
      {
        foreach (var (start, end) in Slots(date, rule, duration, occupied))
        {
          if (start >= utcNow)
          {
            // Two rules of the same day could produce the same start; it is listed once.
            slots.TryAdd(start, new PublicAvailabilitySlotResponse(start, end));
          }
        }
      }
    }

    return slots.Values.ToArray();
  }

  /// <summary>The slots of one rule on one day, in UTC, skipping to the end of whatever occupied time they overlap.</summary>
  internal static IEnumerable<(DateTime StartsAtUtc, DateTime EndsAtUtc)> Slots(
      DateOnly date,
      StaffAvailabilityRule rule,
      TimeSpan duration,
      IReadOnlyCollection<(DateTime StartsAt, DateTime EndsAt)> occupied)
  {
    var ruleEnd = BogotaClock.ToUtc(date, rule.EndTime);
    var start = BogotaClock.ToUtc(date, rule.StartTime);
    while (start + duration <= ruleEnd)
    {
      var end = start + duration;
      DateTime? freeFrom = null;
      foreach (var (busyStart, busyEnd) in occupied)
      {
        if (busyStart < end && start < busyEnd && (freeFrom is null || busyEnd > freeFrom))
        {
          freeFrom = busyEnd;
        }
      }

      if (freeFrom is { } next)
      {
        start = next;
        continue;
      }

      yield return (start, end);
      start = end;
    }
  }
}
