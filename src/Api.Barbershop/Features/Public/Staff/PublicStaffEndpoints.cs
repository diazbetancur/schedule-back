using Barbershop.Application.PublicContent;

namespace Api.Barbershop.Features.Public.Staff;

public static class PublicStaffEndpoints
{
  public static RouteGroupBuilder MapPublicStaffEndpoints(this RouteGroupBuilder api)
  {
    var staff = api.MapGroup("/public/staff")
        .WithTags("Public");

    staff.MapGet(string.Empty, GetPublicStaffAsync)
        .WithName("GetPublicStaffList")
        .Produces<IReadOnlyList<PublicStaffListItemResponse>>(StatusCodes.Status200OK);

    staff.MapGet("/{staffProfileId:guid}", GetPublicStaffByIdAsync)
        .WithName("GetPublicStaffById")
        .Produces<PublicStaffProfileResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound);

    return api;
  }

  private static Task<IReadOnlyList<PublicStaffListItemResponse>> GetPublicStaffAsync(
      string? search,
      IPublicStaffService service,
      CancellationToken cancellationToken)
      => service.GetPublicStaffAsync(search, cancellationToken);

  private static Task<PublicStaffProfileResponse> GetPublicStaffByIdAsync(
      Guid staffProfileId,
      IPublicStaffService service,
      CancellationToken cancellationToken)
      => service.GetPublicStaffByIdAsync(staffProfileId, cancellationToken);
}
