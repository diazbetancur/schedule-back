using Barbershop.Application.Notifications;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Notifications;

internal sealed class PushSubscriptionService : IPushSubscriptionService
{
  private readonly AppDbContext _dbContext;
  private readonly TimeProvider _timeProvider;

  public PushSubscriptionService(AppDbContext dbContext, TimeProvider timeProvider)
  {
    _dbContext = dbContext;
    _timeProvider = timeProvider;
  }

  public async Task SubscribeAsync(Guid currentUserId, PushSubscriptionRequest request, CancellationToken cancellationToken = default)
  {
    var existing = await _dbContext.PushSubscriptions
        .SingleOrDefaultAsync(subscription => subscription.Endpoint == request.Endpoint, cancellationToken);

    if (existing is not null)
    {
      if (existing.UserId == currentUserId)
      {
        return;
      }

      _dbContext.PushSubscriptions.Remove(existing);
    }

    var subscription = new PushSubscription(
        currentUserId,
        request.Endpoint,
        request.P256dhKey,
        request.AuthKey,
        _timeProvider.GetUtcNow().UtcDateTime,
        request.UserAgent);

    _dbContext.PushSubscriptions.Add(subscription);
    await _dbContext.SaveChangesAsync(cancellationToken);
  }

  public async Task UnsubscribeAsync(Guid currentUserId, PushUnsubscribeRequest request, CancellationToken cancellationToken = default)
  {
    var subscription = await _dbContext.PushSubscriptions
        .SingleOrDefaultAsync(
            candidate => candidate.Endpoint == request.Endpoint && candidate.UserId == currentUserId,
            cancellationToken);

    if (subscription is null)
    {
      return;
    }

    _dbContext.PushSubscriptions.Remove(subscription);
    await _dbContext.SaveChangesAsync(cancellationToken);
  }
}
