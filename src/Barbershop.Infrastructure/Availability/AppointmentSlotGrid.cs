using Barbershop.Domain.Common;
using Barbershop.Domain.Scheduling;

namespace Barbershop.Infrastructure.Availability;

/// <summary>
/// Where an appointment booked by a customer may start (ADR-0016 F1): every 15 minutes counted from the start of an
/// availability rule, and the whole service must fit inside that rule (a rule never crosses midnight, so the slot is
/// in one Bogotá day). Availability lists these slots and booking accepts only them.
/// </summary>
internal static class AppointmentSlotGrid
{
    public const int StepMinutes = 15;

    /// <summary>Every slot of <paramref name="rule"/> on <paramref name="date"/> lasting <paramref name="durationMinutes"/>, in UTC.</summary>
    public static IEnumerable<(DateTime StartsAtUtc, DateTime EndsAtUtc)> Slots(DateOnly date, StaffAvailabilityRule rule, int durationMinutes)
    {
        var ruleStart = date.ToDateTime(rule.StartTime);
        var ruleEnd = date.ToDateTime(rule.EndTime);
        for (var start = ruleStart; start.AddMinutes(durationMinutes) <= ruleEnd; start = start.AddMinutes(StepMinutes))
        {
            var startsAtUtc = BogotaClock.ToUtc(date, TimeOnly.FromDateTime(start));
            yield return (startsAtUtc, startsAtUtc.AddMinutes(durationMinutes));
        }
    }

    /// <summary>Whether a local start/end of the same day is one of the slots of <paramref name="rule"/>.</summary>
    public static bool IsSlotOf(StaffAvailabilityRule rule, TimeOnly start, TimeOnly end)
        => rule.StartTime <= start
            && end <= rule.EndTime
            && (start - rule.StartTime).Ticks % TimeSpan.FromMinutes(StepMinutes).Ticks == 0;
}
