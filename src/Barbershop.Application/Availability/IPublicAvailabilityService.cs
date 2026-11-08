namespace Barbershop.Application.Availability;

public interface IPublicAvailabilityService
{
  /// <summary>
  /// Free slots of one professional. Exactly one of <paramref name="generalServiceId"/> (a service they offer: the
  /// slots last its duration) or <paramref name="durationMinutes"/> (10 to 240, to reschedule an appointment that keeps
  /// its reserved duration) is required.
  /// </summary>
  Task<PublicAvailabilitySlotsResponse> GetSlotsAsync(
      Guid staffProfileId,
      DateOnly from,
      DateOnly to,
      Guid? generalServiceId,
      int? durationMinutes,
      CancellationToken cancellationToken = default);
}
