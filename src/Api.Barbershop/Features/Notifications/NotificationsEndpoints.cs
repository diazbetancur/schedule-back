using Api.Barbershop.Configuration;
using Api.Barbershop.Features.Auth;
using Barbershop.Application.Notifications;
using System.Security.Claims;

namespace Api.Barbershop.Features.Notifications;

public static class NotificationsEndpoints
{
  public static RouteGroupBuilder MapNotificationsEndpoints(this RouteGroupBuilder api)
  {
    var notifications = api.MapGroup("/notifications")
        .WithTags("Notifications")
        .RequireAuthorization();

    notifications.MapPost("/subscribe", SubscribeAsync)
        .WithName("SubscribeToPushNotifications")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    notifications.MapPost("/unsubscribe", UnsubscribeAsync)
        .WithName("UnsubscribeFromPushNotifications")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    notifications.MapGet("/push-config", GetPushConfig)
        .WithName("GetPushClientConfig")
        .Produces<PushClientConfigView>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized);

    notifications.MapPost("/test", SendTestAsync)
        .WithName("SendTestPushNotification")
        .RequireRateLimiting(RateLimitPolicyNames.NotificationTest)
        .ProducesProblem(StatusCodes.Status429TooManyRequests)
        .Produces<PushTestResultView>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized);

    // Inbox (bell): latest notifications + unread count.
    notifications.MapGet("/inbox", GetInboxAsync)
        .WithName("GetNotificationInbox")
        .Produces<UserNotificationInboxView>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized);

    notifications.MapPost("/inbox/read-all", MarkAllAsReadAsync)
        .WithName("MarkAllNotificationsAsRead")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status401Unauthorized);

    notifications.MapPost("/inbox/{notificationId:guid}/read", MarkAsReadAsync)
        .WithName("MarkNotificationAsRead")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status404NotFound);

    // ADR-0019 N5: the full history, newest first, paginated.
    notifications.MapGet("/history", GetHistoryAsync)
        .WithName("GetNotificationHistory")
        .Produces<UserNotificationHistoryView>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    // ADR-0019 N4: which categories of notifications reach the user by push.
    notifications.MapGet("/preferences", GetPreferencesAsync)
        .WithName("GetNotificationPreferences")
        .Produces<NotificationPreferencesView>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized);

    notifications.MapPut("/preferences", UpdatePreferencesAsync)
        .WithName("UpdateNotificationPreferences")
        .Produces<NotificationPreferencesView>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    return api;
  }

  private static Task<UserNotificationHistoryView> GetHistoryAsync(
      ClaimsPrincipal user,
      int? page,
      int? pageSize,
      bool? unreadOnly,
      IUserNotificationsService service,
      CancellationToken cancellationToken)
      => service.GetHistoryAsync(user.GetRequiredUserId(), page, pageSize, unreadOnly ?? false, cancellationToken);

  private static Task<NotificationPreferencesView> GetPreferencesAsync(
      ClaimsPrincipal user,
      IUserNotificationsService service,
      CancellationToken cancellationToken)
      => service.GetPreferencesAsync(user.GetRequiredUserId(), cancellationToken);

  private static Task<NotificationPreferencesView> UpdatePreferencesAsync(
      ClaimsPrincipal user,
      NotificationPreferencesUpdateRequest request,
      IUserNotificationsService service,
      CancellationToken cancellationToken)
      => service.UpdatePreferencesAsync(user.GetRequiredUserId(), request, cancellationToken);

  private static Task<UserNotificationInboxView> GetInboxAsync(
      ClaimsPrincipal user,
      int? limit,
      int? offset,
      IUserNotificationsService service,
      CancellationToken cancellationToken)
      => service.GetInboxAsync(user.GetRequiredUserId(), limit ?? 5, cancellationToken, offset ?? 0);

  private static async Task<IResult> MarkAllAsReadAsync(
      ClaimsPrincipal user,
      IUserNotificationsService service,
      CancellationToken cancellationToken)
  {
    await service.MarkAllAsReadAsync(user.GetRequiredUserId(), cancellationToken);
    return Results.NoContent();
  }

  private static async Task<IResult> MarkAsReadAsync(
      ClaimsPrincipal user,
      Guid notificationId,
      IUserNotificationsService service,
      CancellationToken cancellationToken)
  {
    await service.MarkAsReadAsync(user.GetRequiredUserId(), notificationId, cancellationToken);
    return Results.NoContent();
  }

  private static PushClientConfigView GetPushConfig(IPushDiagnosticsService service)
      => service.GetClientConfig();

  private static Task<PushTestResultView> SendTestAsync(
      ClaimsPrincipal user,
      IPushDiagnosticsService service,
      CancellationToken cancellationToken)
      => service.SendTestAsync(user.GetRequiredUserId(), cancellationToken);

  private static async Task<IResult> SubscribeAsync(
      ClaimsPrincipal user,
      PushSubscriptionRequest request,
      IPushSubscriptionService service,
      CancellationToken cancellationToken)
  {
    await service.SubscribeAsync(user.GetRequiredUserId(), request, cancellationToken);
    return Results.NoContent();
  }

  private static async Task<IResult> UnsubscribeAsync(
      ClaimsPrincipal user,
      PushUnsubscribeRequest request,
      IPushSubscriptionService service,
      CancellationToken cancellationToken)
  {
    await service.UnsubscribeAsync(user.GetRequiredUserId(), request, cancellationToken);
    return Results.NoContent();
  }
}
