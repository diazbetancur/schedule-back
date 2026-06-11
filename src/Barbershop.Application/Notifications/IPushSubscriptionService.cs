namespace Barbershop.Application.Notifications;

public interface IPushSubscriptionService
{
    Task SubscribeAsync(Guid currentUserId, PushSubscriptionRequest request, CancellationToken cancellationToken = default);

    Task UnsubscribeAsync(Guid currentUserId, PushUnsubscribeRequest request, CancellationToken cancellationToken = default);
}
