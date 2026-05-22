using Barbershop.Application.Availability;

namespace Api.Barbershop.Features.Public.Availability;

public static class PublicAvailabilityEndpoints
{
  public static RouteGroupBuilder MapPublicAvailabilityEndpoints(this RouteGroupBuilder api)
  {
    var availability = api.MapGroup("/public/staff/{staffProfileId:guid}/availability")
        .WithTags("Public");

    availability.MapGet("/slots", GetSlotsAsync)
        .WithName("GetPublicStaffAvailabilitySlots")
        .Produces<PublicAvailabilitySlotsResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    return api;
  }

  private static Task<PublicAvailabilitySlotsResponse> GetSlotsAsync(
      Guid staffProfileId,
      DateOnly from,
      DateOnly to,
      IPublicAvailabilityService service,
      CancellationToken cancellationToken)
      => service.GetSlotsAsync(staffProfileId, from, to, cancellationToken);
}