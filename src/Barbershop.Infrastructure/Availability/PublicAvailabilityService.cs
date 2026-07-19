using Barbershop.Application.Availability;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Common;
using Barbershop.Domain.Scheduling;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Availability;

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
      CancellationToken cancellationToken = default)
  {
    ValidateRange(from, to);

    var staffProfile = await _dbContext.StaffProfiles
        .Include(profile => profile.User)
        .SingleOrDefaultAsync(profile => profile.Id == staffProfileId, cancellationToken)
        ?? throw new KeyNotFoundException("The staff profile was not found.");

    if (!staffProfile.IsActive || !staffProfile.User.IsActive)
    {
      throw new KeyNotFoundException("The staff profile was not found.");
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

    var slots = new List<PublicAvailabilitySlotResponse>();
    for (var date = from; date <= to; date = date.AddDays(1))
    {
      if (!rulesByDay.TryGetValue(date.DayOfWeek, out var dayRules))
      {
        continue;
      }

      foreach (var rule in dayRules)
      {
        var slotStart = BogotaClock.ToUtc(date, rule.StartTime);
        var ruleEnd = BogotaClock.ToUtc(date, rule.EndTime);

        while (slotStart.AddMinutes(staffProfile.DefaultAppointmentDurationMinutes) <= ruleEnd)
        {
          var slotEnd = slotStart.AddMinutes(staffProfile.DefaultAppointmentDurationMinutes);

          if (slotStart >= utcNow
              && unavailablePeriods.All(period => !period.Overlaps(slotStart, slotEnd))
              && appointments.All(appointment => !appointment.Overlaps(slotStart, slotEnd)))
          {
            slots.Add(new PublicAvailabilitySlotResponse(slotStart, slotEnd));
          }

          slotStart = slotEnd;
        }
      }
    }

    return new PublicAvailabilitySlotsResponse(
        staffProfileId,
        from,
        to,
        staffProfile.DefaultAppointmentDurationMinutes,
        slots);
  }

  private static void ValidateRange(DateOnly from, DateOnly to)
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

    if (errors.Count > 0)
    {
      throw new ValidationProblemException(errors);
    }
  }
}