namespace Barbershop.Application.Notifications;

public interface IUserNotificationsService
{
    /// <summary>Latest <paramref name="limit"/> notifications of the user plus the total unread count.</summary>
    Task<UserNotificationInboxView> GetInboxAsync(Guid currentUserId, int limit, CancellationToken cancellationToken = default);

    Task MarkAsReadAsync(Guid currentUserId, Guid notificationId, CancellationToken cancellationToken = default);

    Task MarkAllAsReadAsync(Guid currentUserId, CancellationToken cancellationToken = default);
}
