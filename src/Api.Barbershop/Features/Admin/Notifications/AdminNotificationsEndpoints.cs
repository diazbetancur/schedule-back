using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Barbershop.Application.Notifications;
using System.Security.Claims;

namespace Api.Barbershop.Features.Admin.Notifications;

public static class AdminNotificationsEndpoints
{
  public static RouteGroupBuilder MapAdminNotificationsEndpoints(this RouteGroupBuilder api)
  {
    var notifications = api.MapGroup("/admin/notifications")
        .WithTags("Admin")
        .RequireAuthorization(AuthPolicyNames.Admin);

    notifications.MapPost("/broadcast", BroadcastAsync)
        .WithName("BroadcastAdminNotification")
        .Produces<NotificationCampaignView>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    notifications.MapGet("/campaigns", GetCampaignsAsync)
        .WithName("GetAdminNotificationCampaigns")
        .Produces<IReadOnlyList<NotificationCampaignView>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    notifications.MapGet("/customers/search", SearchCustomersAsync)
        .WithName("SearchAdminNotificationCustomers")
        .Produces<IReadOnlyList<CustomerSummaryView>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    return api;
  }

  private static async Task<IResult> BroadcastAsync(
      ClaimsPrincipal user,
      NotificationBroadcastRequest request,
      IAdminNotificationsService service,
      CancellationToken cancellationToken)
  {
    var response = await service.BroadcastAsync(user.GetRequiredUserId(), request, cancellationToken);
    return Results.Created($"/api/v1/admin/notifications/campaigns/{response.Id}", response);
  }

  private static Task<IReadOnlyList<NotificationCampaignView>> GetCampaignsAsync(
      IAdminNotificationsService service,
      CancellationToken cancellationToken)
      => service.GetCampaignsAsync(cancellationToken);

  private static Task<IReadOnlyList<CustomerSummaryView>> SearchCustomersAsync(
      string? search,
      IAdminNotificationsService service,
      CancellationToken cancellationToken)
      => service.SearchCustomersAsync(search, cancellationToken);
}
