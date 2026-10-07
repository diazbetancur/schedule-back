using Barbershop.Application.Notifications;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Barbershop.Infrastructure.Notifications;

internal sealed class NotificationDispatcher : INotificationDispatcher
{
  private static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromHours(24);

  private readonly AppDbContext _dbContext;
  private readonly TimeProvider _timeProvider;
  private readonly NotificationWorkerSignal _workerSignal;
  private readonly ILogger<NotificationDispatcher> _logger;

  public NotificationDispatcher(
      AppDbContext dbContext,
      TimeProvider timeProvider,
      NotificationWorkerSignal workerSignal,
      ILogger<NotificationDispatcher> logger)
  {
    _dbContext = dbContext;
    _timeProvider = timeProvider;
    _workerSignal = workerSignal;
    _logger = logger;
  }

  public Task DispatchAsync(
      IReadOnlyCollection<Guid> userIds,
      string type,
      PushNotificationMessage message,
      CancellationToken cancellationToken = default)
      => DispatchAsync(userIds, type, message, new NotificationDispatchOptions(), cancellationToken);

  public async Task<int> DispatchAsync(
      IReadOnlyCollection<Guid> userIds,
      string type,
      PushNotificationMessage message,
      NotificationDispatchOptions options,
      CancellationToken cancellationToken = default)
  {
    var recipients = userIds.Distinct().ToList();
    if (recipients.Count == 0)
    {
      return 0;
    }

    var dedupKeys = options.DedupKey is { Length: > 0 } dedupKey
        ? recipients.ToDictionary(userId => userId, userId => BuildDedupKey(dedupKey, userId))
        : null;

    if (dedupKeys is not null)
    {
      var keysByUser = dedupKeys;
      var candidateKeys = keysByUser.Values.ToList();
      var existingKeys = await _dbContext.UserNotifications
          .Where(notification => notification.DedupKey != null && candidateKeys.Contains(notification.DedupKey))
          .Select(notification => notification.DedupKey!)
          .ToListAsync(cancellationToken);

      recipients.RemoveAll(userId => existingKeys.Contains(keysByUser[userId]));
      if (recipients.Count == 0)
      {
        return 0;
      }
    }

    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
    var url = WebPushPayloadBuilder.NormalizeUrl(message.Url);
    var expiresAtUtc = options.ExpiresAtUtc ?? nowUtc + (message.TimeToLive ?? DefaultTimeToLive);
    if (expiresAtUtc <= nowUtc)
    {
      expiresAtUtc = nowUtc.AddMinutes(1);
    }

    var added = new List<object>(recipients.Count * 2);
    foreach (var userId in recipients)
    {
      var inboxEntry = new UserNotification(userId, type, message.Title, message.Body, url, nowUtc, dedupKeys?[userId]);
      var delivery = new NotificationDelivery(
          userId,
          inboxEntry.Id,
          NotificationDeliveryChannel.Push,
          message.Title,
          message.Body,
          url,
          message.Tag,
          message.IsTimeSensitive,
          nowUtc,
          expiresAtUtc);

      _dbContext.UserNotifications.Add(inboxEntry);
      _dbContext.NotificationDeliveries.Add(delivery);
      added.Add(inboxEntry);
      added.Add(delivery);
    }

    try
    {
      await _dbContext.SaveChangesAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
      // The appointment (or campaign) is already saved; a lost notification must not fail the request.
      // With a dedup key this is usually a concurrent run that already created the same reminder.
      foreach (var entity in added)
      {
        _dbContext.Entry(entity).State = EntityState.Detached;
      }

      _logger.LogWarning(exception, "Could not queue {Count} notification(s) of type {Type}.", recipients.Count, type);
      return 0;
    }

    _workerSignal.Notify();
    return recipients.Count;
  }

  internal static string BuildDedupKey(string dedupKey, Guid userId) => $"{dedupKey}:{userId:N}";
}
