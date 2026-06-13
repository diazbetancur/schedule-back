using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Barbershop.Application.Notifications;
using Barbershop.Infrastructure.Configuration;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebPush;

namespace Barbershop.Infrastructure.Notifications;

internal sealed class WebPushNotificationSender : IPushNotificationSender
{
  private const string NotificationIconPath = "/icons/icon-192x192.png";
  private const string NotificationBadgePath = "/icons/icon-72x72.png";

  private readonly AppDbContext _dbContext;
  private readonly WebPushOptions _options;
  private readonly WebPushClient _client;
  private readonly ILogger<WebPushNotificationSender> _logger;

  public WebPushNotificationSender(
      AppDbContext dbContext,
      IOptions<WebPushOptions> options,
      WebPushClient client,
      ILogger<WebPushNotificationSender> logger)
  {
    _dbContext = dbContext;
    _options = options.Value;
    _client = client;
    _logger = logger;
  }

  public async Task SendToUsersAsync(IReadOnlyCollection<Guid> userIds, PushNotificationMessage message, CancellationToken cancellationToken = default)
  {
    if (!_options.Enabled || userIds.Count == 0)
    {
      return;
    }

    var subscriptions = await _dbContext.PushSubscriptions
        .Where(subscription => userIds.Contains(subscription.UserId))
        .ToListAsync(cancellationToken);

    if (subscriptions.Count == 0)
    {
      return;
    }

    var vapidDetails = new VapidDetails(
        $"mailto:{_options.ContactEmail}",
        _options.PublicKey,
        _options.PrivateKey);

    var payload = JsonSerializer.Serialize(new
    {
      notification = new
      {
        title = message.Title,
        body = message.Body,
        icon = NotificationIconPath,
        badge = NotificationBadgePath,
        data = message.Url is not null ? new { url = message.Url } : (object?)null,
      },
    }, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

    var staleSubscriptionIds = new List<Guid>();

    foreach (var subscription in subscriptions)
    {
      var pushSubscription = new PushSubscription(subscription.Endpoint, subscription.P256dhKey, subscription.AuthKey);

      try
      {
        await _client.SendNotificationAsync(pushSubscription, payload, vapidDetails, cancellationToken);
      }
      catch (WebPushException webPushException)
          when (webPushException.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
      {
        staleSubscriptionIds.Add(subscription.Id);
      }
      catch (WebPushException webPushException)
      {
        _logger.LogWarning(
            webPushException,
            "Push delivery failed for subscription {SubscriptionId} with status {StatusCode}.",
            subscription.Id,
            webPushException.StatusCode);
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        _logger.LogWarning(
            ex,
            "Unexpected error delivering push to subscription {SubscriptionId}.",
            subscription.Id);
      }
    }

    if (staleSubscriptionIds.Count > 0)
    {
      await _dbContext.PushSubscriptions
          .Where(subscription => staleSubscriptionIds.Contains(subscription.Id))
          .ExecuteDeleteAsync(cancellationToken);
    }
  }
}
