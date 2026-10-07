namespace Barbershop.Application.Notifications;

public interface INotificationDispatcher
{
    /// <summary>
    /// Saves the notification in each recipient's inbox (the bell) and sends it as push to their devices.
    /// Best-effort: failures are logged and never undo the business operation that triggered it.
    /// </summary>
    Task DispatchAsync(
        IReadOnlyCollection<Guid> userIds,
        string type,
        PushNotificationMessage message,
        CancellationToken cancellationToken = default);
}
