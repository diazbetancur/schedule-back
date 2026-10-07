namespace Barbershop.Application.Notifications;

public sealed record PushSubscriptionRequest(
    string Endpoint,
    string P256dhKey,
    string AuthKey,
    string? UserAgent = null);

public sealed record PushUnsubscribeRequest(string Endpoint);

/// <summary>A push message for one or more users.</summary>
/// <param name="Title">Notification title.</param>
/// <param name="Body">Notification body.</param>
/// <param name="Url">App-relative path opened when the notification is tapped. Null or non-relative values fall back to "/".</param>
/// <param name="Tag">Groups notifications on the device: a new one with the same tag replaces the previous one (e.g. "appointment-{id}").</param>
/// <param name="IsTimeSensitive">True sends <c>Urgency: high</c> (wakes the device); false sends <c>normal</c> (campaigns).</param>
/// <param name="TimeToLive">How long the push service keeps retrying while the device is offline. Null means 24 hours.</param>
public sealed record PushNotificationMessage(
    string Title,
    string Body,
    string? Url = null,
    string? Tag = null,
    bool IsTimeSensitive = true,
    TimeSpan? TimeToLive = null);

/// <summary>What the browser needs to create a push subscription. The VAPID public key is not a secret.</summary>
public sealed record PushClientConfigView(bool Enabled, string? VapidPublicKey);

public sealed record PushTestResultView(
    bool Enabled,
    int DeviceCount,
    int DeliveredCount,
    IReadOnlyList<PushDeviceResultView> Devices,
    IReadOnlyList<string> Warnings);

public sealed record PushDeviceResultView(
    Guid SubscriptionId,
    string PushService,
    string? UserAgent,
    bool Delivered,
    int? StatusCode,
    string? Error,
    bool Removed);
