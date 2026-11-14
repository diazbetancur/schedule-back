namespace Barbershop.Application.Notifications;

public interface IUserNotificationsService
{
    /// <summary>Latest <paramref name="limit"/> notifications of the user plus the total unread count.</summary>
    /// <summary>Unread first, then the most recent; <paramref name="offset"/> pages through the rest (BUG-107).</summary>
    Task<UserNotificationInboxView> GetInboxAsync(Guid currentUserId, int limit, CancellationToken cancellationToken = default, int offset = 0);

    Task MarkAsReadAsync(Guid currentUserId, Guid notificationId, CancellationToken cancellationToken = default);

    Task MarkAllAsReadAsync(Guid currentUserId, CancellationToken cancellationToken = default);
}
