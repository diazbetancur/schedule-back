namespace Barbershop.Application.Notifications;

public interface IPushNotificationSender
{
    /// <summary>
    /// Sends a push message to every subscribed device of the given users.
    /// Best-effort: delivery failures are logged and do not throw.
    /// </summary>
    Task SendToUsersAsync(IReadOnlyCollection<Guid> userIds, PushNotificationMessage message, CancellationToken cancellationToken = default);
}
