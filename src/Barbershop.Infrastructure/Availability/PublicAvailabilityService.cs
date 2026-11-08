using Barbershop.Application.Availability;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Common;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Staff;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Availability;

/// <summary>
/// Free slots of a professional for one service (ADR-0016 D10, F1): each slot lasts what that service takes them,
/// starts on the 15-minute grid of an availability rule and fits inside it, and does not overlap an absence or a
/// pending/confirmed appointment (appointments from before services keep their own EndsAt). No price (D9).
/// </summary>
internal sealed class PublicAvailabilityService : IPublicAvailabilityService
{
  private readonly AppDbContext _dbContext;
  private readonly TimeProvider _timeProvider;

  public PublicAvailabilityService(AppDbContext dbContext, TimeProvider timeProvider)
  {
    _dbContext = dbContext;
    _timeProvider = timeProvider;
  }

  public async Task<PublicAvailabilitySlotsResponse> GetSlotsAsync(
      Guid staffProfileId,
      DateOnly from,
      DateOnly to,
      Guid? generalServiceId,
      int? durationMinutes,
      CancellationToken cancellationToken = default)
  {
    ValidateRequest(from, to, generalServiceId, durationMinutes);

    var staffProfile = await _dbContext.StaffProfiles
        .Include(profile => profile.User)
        .SingleOrDefaultAsync(profile => profile.Id == staffProfileId, cancellationToken)
        ?? throw new KeyNotFoundException("The staff profile was not found.");

    if (!staffProfile.IsActive || !staffProfile.User.IsActive)
    {
      throw new KeyNotFoundException("The staff profile was not found.");
    }

    var duration = durationMinutes ?? 0;
    if (generalServiceId is { } serviceId)
    {
      var offering = await _dbContext.FindBookableAsync(staffProfileId, serviceId, cancellationToken)
          ?? throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
          {
            ["generalServiceId"] = ["El profesional no ofrece este servicio."]
          });
      duration = offering.DurationMinutes;
    }

    var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
    var rangeStart = BogotaClock.ToUtc(from, TimeOnly.MinValue);
    var rangeEndExclusive = BogotaClock.ToUtc(to.AddDays(1), TimeOnly.MinValue);

    var rules = await _dbContext.StaffAvailabilityRules
        .Where(rule => rule.StaffProfileId == staffProfileId && rule.IsActive)
        .OrderBy(rule => rule.DayOfWeek)
        .ThenBy(rule => rule.StartTime)
        .ToListAsync(cancellationToken);

    var unavailablePeriods = await _dbContext.StaffUnavailablePeriods
        .Where(period => period.StaffProfileId == staffProfileId && period.EndsAt > rangeStart && period.StartsAt < rangeEndExclusive)
        .ToListAsync(cancellationToken);

    var appointments = await _dbContext.Appointments
        .Where(appointment => appointment.StaffProfileId == staffProfileId
            && (appointment.Status == AppointmentStatus.Pending || appointment.Status == AppointmentStatus.Confirmed)
            && appointment.EndsAt > rangeStart
            && appointment.StartsAt < rangeEndExclusive)
        .ToListAsync(cancellationToken);

    var rulesByDay = rules
        .GroupBy(rule => rule.DayOfWeek)
        .ToDictionary(group => group.Key, group => group.ToArray());

    var slots = new SortedDictionary<DateTime, PublicAvailabilitySlotResponse>();
    for (var date = from; date <= to; date = date.AddDays(1))
    {
      if (!rulesByDay.TryGetValue(date.DayOfWeek, out var dayRules))
      {
        continue;
      }

      foreach (var rule in dayRules)
      {
        foreach (var (slotStart, slotEnd) in AppointmentSlotGrid.Slots(date, rule, duration))
        {
          if (slotStart >= utcNow
              && unavailablePeriods.All(period => !period.Overlaps(slotStart, slotEnd))
              && appointments.All(appointment => !appointment.Overlaps(slotStart, slotEnd)))
          {
            // Two rules of the same day could produce the same start; it is listed once.
            slots.TryAdd(slotStart, new PublicAvailabilitySlotResponse(slotStart, slotEnd));
          }
        }
      }
    }

    return new PublicAvailabilitySlotsResponse(
        staffProfileId,
        from,
        to,
        duration,
        slots.Values.ToArray(),
        generalServiceId,
        AppointmentSlotGrid.StepMinutes);
  }

  private static void ValidateRequest(DateOnly from, DateOnly to, Guid? generalServiceId, int? durationMinutes)
  {
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    if (from == default)
    {
      errors["from"] = ["From is required."];
    }

    if (to == default)
    {
      errors["to"] = ["To is required."];
    }

    if (to < from)
    {
      errors["to"] = ["To must be on or after From."];
    }
    else if (to.DayNumber - from.DayNumber > 30)
    {
      errors["to"] = ["The availability range cannot exceed 31 days."];
    }

    if (generalServiceId is null && durationMinutes is null)
    {
      errors["generalServiceId"] = ["Elige el servicio que quieres reservar."];
    }
    else if (generalServiceId is not null && durationMinutes is not null)
    {
      errors["durationMinutes"] = ["Indica el servicio o la duración, no ambos."];
    }
    else if (generalServiceId == Guid.Empty)
    {
      errors["generalServiceId"] = ["Elige el servicio que quieres reservar."];
    }
    else if (durationMinutes is { } minutes
        && minutes is < AppointmentServiceReservation.MinDurationMinutes or > AppointmentServiceReservation.MaxDurationMinutes)
    {
      errors["durationMinutes"] =
          [$"La duración debe estar entre {AppointmentServiceReservation.MinDurationMinutes} y {AppointmentServiceReservation.MaxDurationMinutes} minutos."];
    }

    if (errors.Count > 0)
    {
      throw new ValidationProblemException(errors);
    }
  }
}
