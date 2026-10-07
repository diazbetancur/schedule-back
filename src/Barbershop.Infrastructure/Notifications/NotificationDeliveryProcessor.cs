using Barbershop.Application.Notifications;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Barbershop.Infrastructure.Notifications;

/// <summary>Sends due push deliveries from the queue. Run by <see cref="NotificationBackgroundWorker"/>.</summary>
internal sealed class NotificationDeliveryProcessor
{
  internal static readonly TimeSpan CompletedDeliveryRetention = TimeSpan.FromDays(14);
  internal static readonly TimeSpan InboxRetention = TimeSpan.FromDays(90);
  private static readonly TimeSpan MinimumTimeToLive = TimeSpan.FromMinutes(1);

  private readonly AppDbContext _dbContext;
  private readonly IPushDeliveryChannel _pushChannel;
  private readonly TimeProvider _timeProvider;
  private readonly ILogger<NotificationDeliveryProcessor> _logger;

  public NotificationDeliveryProcessor(
      AppDbContext dbContext,
      IPushDeliveryChannel pushChannel,
      TimeProvider timeProvider,
      ILogger<NotificationDeliveryProcessor> logger)
  {
    _dbContext = dbContext;
    _pushChannel = pushChannel;
    _timeProvider = timeProvider;
    _logger = logger;
  }

  /// <returns>How many deliveries were processed (equal to <paramref name="batchSize"/> means there may be more).</returns>
  public async Task<int> ProcessDueAsync(int batchSize, CancellationToken cancellationToken = default)
  {
    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

    var due = await _dbContext.NotificationDeliveries
        .Where(delivery => delivery.Status == NotificationDeliveryStatus.Pending && delivery.NextAttemptAt <= nowUtc)
        .OrderBy(delivery => delivery.NextAttemptAt)
        .Take(batchSize)
        .ToListAsync(cancellationToken);

    foreach (var delivery in due)
    {
      await ProcessOneAsync(delivery, cancellationToken);

      // Save after each one so a crash mid-batch never re-sends what already went out.
      await _dbContext.SaveChangesAsync(cancellationToken);
    }

    return due.Count;
  }

  /// <summary>Deletes finished deliveries after 14 days and inbox entries after 90 days.</summary>
  public async Task CleanupAsync(CancellationToken cancellationToken = default)
  {
    if (!_dbContext.Database.IsRelational())
    {
      return;
    }

    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
    var deliveriesBefore = nowUtc - CompletedDeliveryRetention;
    var inboxBefore = nowUtc - InboxRetention;

    var deletedDeliveries = await _dbContext.NotificationDeliveries
        .Where(delivery => delivery.Status != NotificationDeliveryStatus.Pending && delivery.CreatedAt < deliveriesBefore)
        .ExecuteDeleteAsync(cancellationToken);

    var deletedInbox = await _dbContext.UserNotifications
        .Where(notification => notification.CreatedAt < inboxBefore)
        .ExecuteDeleteAsync(cancellationToken);

    if (deletedDeliveries + deletedInbox > 0)
    {
      _logger.LogInformation(
          "Notification cleanup removed {Deliveries} deliveries and {Inbox} inbox entries.",
          deletedDeliveries,
          deletedInbox);
    }
  }

  private async Task ProcessOneAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
  {
    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

    if (delivery.ExpiresAt <= nowUtc)
    {
      delivery.MarkSkipped(nowUtc, "Expired before it could be delivered.");
      return;
    }

    var timeToLive = delivery.ExpiresAt - nowUtc;
    var message = new PushNotificationMessage(
        delivery.Title,
        delivery.Body,
        delivery.Url,
        delivery.Tag,
        delivery.IsTimeSensitive,
        timeToLive < MinimumTimeToLive ? MinimumTimeToLive : timeToLive);

    var alreadyDelivered = delivery.GetDeliveredTargets();

    PushChannelResult result;
    try
    {
      result = await _pushChannel.SendToUserAsync(delivery.UserId, message, alreadyDelivered, cancellationToken);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
      _logger.LogWarning(exception, "Push delivery {DeliveryId} failed unexpectedly.", delivery.Id);
      delivery.ScheduleRetry(nowUtc, exception.Message, []);
      return;
    }

    if (!result.Enabled)
    {
      delivery.MarkSkipped(nowUtc, "WebPush is disabled on the server.");
      return;
    }

    if (result.DeviceCount == 0)
    {
      if (alreadyDelivered.Count > 0)
      {
        delivery.MarkSent(nowUtc, []);
      }
      else
      {
        // Normal: the user never enabled push. The notification is still in their inbox (bell).
        delivery.MarkSkipped(nowUtc, "The user has no push devices.");
      }

      return;
    }

    if (result.TransientFailures > 0)
    {
      delivery.ScheduleRetry(nowUtc, result.LastError, result.DeliveredSubscriptionIds);
      return;
    }

    if (result.DeliveredSubscriptionIds.Count == 0 && alreadyDelivered.Count == 0 && result.PermanentFailures > 0)
    {
      delivery.MarkFailed(nowUtc, result.LastError, []);
      return;
    }

    delivery.MarkSent(nowUtc, result.DeliveredSubscriptionIds);
  }
}
