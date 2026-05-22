namespace Barbershop.Application.Availability;

public interface IPublicAvailabilityService
{
  Task<PublicAvailabilitySlotsResponse> GetSlotsAsync(
      Guid staffProfileId,
      DateOnly from,
      DateOnly to,
      CancellationToken cancellationToken = default);
}