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

    return api;
  }

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
