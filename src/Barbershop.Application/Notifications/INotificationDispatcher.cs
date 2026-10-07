namespace Barbershop.Application.Notifications;

/// <param name="DedupKey">If set, a recipient that already has a notification with this key (per user) is skipped. Used by scheduled reminders.</param>
/// <param name="ExpiresAtUtc">After this instant the push is no longer worth delivering (e.g. the appointment already started). Default: 24 h.</param>
public sealed record NotificationDispatchOptions(string? DedupKey = null, DateTime? ExpiresAtUtc = null);

public interface INotificationDispatcher
{
    /// <summary>
    /// Saves the notification in each recipient's inbox (the bell) and queues a push delivery for them.
    /// Delivery happens in the background with retries. Best-effort: failures are logged and never
    /// undo the business operation that triggered it.
    /// </summary>
    Task DispatchAsync(
        IReadOnlyCollection<Guid> userIds,
        string type,
        PushNotificationMessage message,
        CancellationToken cancellationToken = default);

    /// <returns>How many recipients got a new notification (0 when all were deduplicated).</returns>
    Task<int> DispatchAsync(
        IReadOnlyCollection<Guid> userIds,
        string type,
        PushNotificationMessage message,
        NotificationDispatchOptions options,
        CancellationToken cancellationToken = default);
}
