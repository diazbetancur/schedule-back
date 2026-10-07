using Barbershop.Application.Notifications;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Barbershop.Infrastructure.Notifications;

internal sealed class NotificationDispatcher : INotificationDispatcher
{
  private readonly AppDbContext _dbContext;
  private readonly IPushNotificationSender _pushSender;
  private readonly TimeProvider _timeProvider;
  private readonly ILogger<NotificationDispatcher> _logger;

  public NotificationDispatcher(
      AppDbContext dbContext,
      IPushNotificationSender pushSender,
      TimeProvider timeProvider,
      ILogger<NotificationDispatcher> logger)
  {
    _dbContext = dbContext;
    _pushSender = pushSender;
    _timeProvider = timeProvider;
    _logger = logger;
  }

  public async Task DispatchAsync(
      IReadOnlyCollection<Guid> userIds,
      string type,
      PushNotificationMessage message,
      CancellationToken cancellationToken = default)
  {
    var recipients = userIds.Distinct().ToArray();
    if (recipients.Length == 0)
    {
      return;
    }

    await SaveToInboxAsync(recipients, type, message, cancellationToken);
    await _pushSender.SendToUsersAsync(recipients, message, cancellationToken);
  }

  private async Task SaveToInboxAsync(
      IReadOnlyCollection<Guid> recipients,
      string type,
      PushNotificationMessage message,
      CancellationToken cancellationToken)
  {
    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
    var url = WebPushPayloadBuilder.NormalizeUrl(message.Url);

    var notifications = recipients
        .Select(userId => new UserNotification(userId, type, message.Title, message.Body, url, nowUtc))
        .ToList();

    _dbContext.UserNotifications.AddRange(notifications);

    try
    {
      await _dbContext.SaveChangesAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
      // The appointment (or campaign) is already saved; losing the inbox entry must not fail the request.
      foreach (var notification in notifications)
      {
        _dbContext.Entry(notification).State = EntityState.Detached;
      }

      _logger.LogWarning(exception, "Could not save {Count} inbox notification(s) of type {Type}.", notifications.Count, type);
    }
  }
}
