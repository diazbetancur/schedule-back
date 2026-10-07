namespace Barbershop.Application.Notifications;

/// <summary>Outcome of sending one notification to all push devices of one user.</summary>
/// <param name="Enabled">False when WebPush is turned off on the server.</param>
/// <param name="DeviceCount">Devices attempted (excluding the ones skipped because they already received it).</param>
/// <param name="DeliveredSubscriptionIds">Devices that accepted the message.</param>
/// <param name="TransientFailures">Failures worth retrying (429, 5xx, network).</param>
/// <param name="PermanentFailures">Failures that will not improve with a retry (400, 401, 403, 413...).</param>
public sealed record PushChannelResult(
    bool Enabled,
    int DeviceCount,
    IReadOnlyList<Guid> DeliveredSubscriptionIds,
    int TransientFailures,
    int PermanentFailures,
    string? LastError);

public interface IPushDeliveryChannel
{
    Task<PushChannelResult> SendToUserAsync(
        Guid userId,
        PushNotificationMessage message,
        IReadOnlyCollection<Guid> skipSubscriptionIds,
        CancellationToken cancellationToken = default);
}
