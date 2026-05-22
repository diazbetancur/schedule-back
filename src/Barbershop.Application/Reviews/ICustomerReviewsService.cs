namespace Barbershop.Application.Reviews;

public interface ICustomerReviewsService
{
  Task<IReadOnlyList<CustomerReviewView>> GetByCurrentCustomerAsync(Guid currentUserId, CancellationToken cancellationToken = default);

  Task<CustomerReviewView> CreateAsync(Guid currentUserId, Guid appointmentId, ReviewCreateRequest request, CancellationToken cancellationToken = default);
}
