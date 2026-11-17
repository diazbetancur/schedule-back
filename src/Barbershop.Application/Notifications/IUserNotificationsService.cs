namespace Barbershop.Application.Notifications;

public interface IUserNotificationsService
{
    /// <summary>Latest <paramref name="limit"/> notifications of the user plus the total unread count.</summary>
    /// <summary>Unread first, then the most recent; <paramref name="offset"/> pages through the rest (BUG-107).</summary>
    Task<UserNotificationInboxView> GetInboxAsync(Guid currentUserId, int limit, CancellationToken cancellationToken = default, int offset = 0);

    Task MarkAsReadAsync(Guid currentUserId, Guid notificationId, CancellationToken cancellationToken = default);

    Task MarkAllAsReadAsync(Guid currentUserId, CancellationToken cancellationToken = default);

    /// <summary>ADR-0019 N5: every notification of the user, newest first, paginated; optionally only the unread ones.</summary>
    Task<UserNotificationHistoryView> GetHistoryAsync(
        Guid currentUserId, int? page, int? pageSize, bool unreadOnly, CancellationToken cancellationToken = default);

    /// <summary>ADR-0019 N4: the push preference of each optional category.</summary>
    Task<NotificationPreferencesView> GetPreferencesAsync(Guid currentUserId, CancellationToken cancellationToken = default);

    Task<NotificationPreferencesView> UpdatePreferencesAsync(
        Guid currentUserId, NotificationPreferencesUpdateRequest request, CancellationToken cancellationToken = default);
}
