using Barbershop.Application.Availability;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Common;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Staff;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Availability;

/// <summary>
/// Free slots of a professional for one service (ADR-0016 D10): each slot lasts what that service takes them and is
/// placed by <see cref="AppointmentSlotGrid"/> (ADR-0022: one start every duration from the start of each rule,
/// resuming where an absence or a pending/confirmed appointment ends). No price (D9).
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
        ?? throw new KeyNotFoundException("No encontramos este profesional.");

    if (!staffProfile.IsActive || !staffProfile.User.IsActive)
    {
      throw new KeyNotFoundException("No encontramos este profesional.");
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

    var slots = await AppointmentSlotGrid.FindAsync(
        _dbContext,
        staffProfileId,
        from,
        to,
        TimeSpan.FromMinutes(duration),
        null,
        _timeProvider.GetUtcNow().UtcDateTime,
        cancellationToken);

    // ADR-0022 P1: the nominal step is the duration; after an occupied time the grid resumes where it ends (P3).
    return new PublicAvailabilitySlotsResponse(staffProfileId, from, to, duration, slots, generalServiceId, duration);
  }

  private static void ValidateRequest(DateOnly from, DateOnly to, Guid? generalServiceId, int? durationMinutes)
  {
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
    AddRangeErrors(errors, from, to);

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

  /// <summary>The date range rules shared by every slots query (also the customer's reschedule slots).</summary>
  internal static void AddRangeErrors(IDictionary<string, string[]> errors, DateOnly from, DateOnly to)
  {
    if (from == default)
    {
      errors["from"] = ["Indica la fecha inicial."];
    }

    if (to == default)
    {
      errors["to"] = ["Indica la fecha final."];
    }

    if (to < from)
    {
      errors["to"] = ["La fecha final no puede ser anterior a la inicial."];
    }
    else if (to.DayNumber - from.DayNumber > 30)
    {
      errors["to"] = ["Consulta como máximo 31 días de disponibilidad a la vez."];
    }
  }
}
