namespace Barbershop.Application.Notifications;

public sealed record PushSubscriptionRequest(
    string Endpoint,
    string P256dhKey,
    string AuthKey,
    string? UserAgent = null);

public sealed record PushUnsubscribeRequest(string Endpoint);

public sealed record PushNotificationMessage(string Title, string Body);
