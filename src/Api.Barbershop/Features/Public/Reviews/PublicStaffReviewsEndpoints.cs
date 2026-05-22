using Barbershop.Application.Reviews;

namespace Api.Barbershop.Features.Public.Reviews;

public static class PublicStaffReviewsEndpoints
{
  public static RouteGroupBuilder MapPublicStaffReviewsEndpoints(this RouteGroupBuilder api)
  {
    var reviews = api.MapGroup("/public/staff/{staffProfileId:guid}/reviews")
        .WithTags("Public");

    reviews.MapGet(string.Empty, GetByStaffProfileAsync)
        .WithName("GetPublicStaffReviews")
        .Produces<IReadOnlyList<PublicStaffReviewView>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound);

    reviews.MapGet("/summary", GetSummaryByStaffProfileAsync)
        .WithName("GetPublicStaffReviewsSummary")
        .Produces<PublicStaffReviewsSummaryView>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound);

    return api;
  }

  private static Task<IReadOnlyList<PublicStaffReviewView>> GetByStaffProfileAsync(
      Guid staffProfileId,
      IPublicReviewsService service,
      CancellationToken cancellationToken)
      => service.GetByStaffProfileAsync(staffProfileId, cancellationToken);

  private static Task<PublicStaffReviewsSummaryView> GetSummaryByStaffProfileAsync(
      Guid staffProfileId,
      IPublicReviewsService service,
      CancellationToken cancellationToken)
      => service.GetSummaryByStaffProfileAsync(staffProfileId, cancellationToken);
}
