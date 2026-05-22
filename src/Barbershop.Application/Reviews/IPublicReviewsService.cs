namespace Barbershop.Application.Reviews;

public interface IPublicReviewsService
{
  Task<IReadOnlyList<PublicStaffReviewView>> GetByStaffProfileAsync(Guid staffProfileId, CancellationToken cancellationToken = default);

  Task<PublicStaffReviewsSummaryView> GetSummaryByStaffProfileAsync(Guid staffProfileId, CancellationToken cancellationToken = default);
}
