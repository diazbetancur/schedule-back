using Barbershop.Application.Notifications;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Notifications;

internal sealed class UserNotificationsService : IUserNotificationsService
{
  internal const int DefaultLimit = 5;
  internal const int MaxLimit = 50;

  private readonly AppDbContext _dbContext;
  private readonly TimeProvider _timeProvider;

  public UserNotificationsService(AppDbContext dbContext, TimeProvider timeProvider)
  {
    _dbContext = dbContext;
    _timeProvider = timeProvider;
  }

  public async Task<UserNotificationInboxView> GetInboxAsync(
      Guid currentUserId, int limit, CancellationToken cancellationToken = default, int offset = 0)
  {
    var take = limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);
    var skip = Math.Max(0, offset);

    var unreadCount = await _dbContext.UserNotifications
        .CountAsync(notification => notification.UserId == currentUserId && notification.ReadAt == null, cancellationToken);

    // BUG-107: the badge counted every unread notification while the panel only showed the 5 most recent (often
    // already read). Unread ones come first, then the most recent; one more row than asked tells whether there is more.
    var items = await _dbContext.UserNotifications
        .AsNoTracking()
        .Where(notification => notification.UserId == currentUserId)
        .OrderBy(notification => notification.ReadAt == null ? 0 : 1)
        .ThenByDescending(notification => notification.CreatedAt)
        .ThenByDescending(notification => notification.Id)
        .Skip(skip)
        .Take(take + 1)
        .Select(notification => new UserNotificationView(
            notification.Id,
            notification.Type,
            notification.Title,
            notification.Body,
            notification.Url,
            notification.CreatedAt,
            notification.ReadAt))
        .ToListAsync(cancellationToken);

    var hasMore = items.Count > take;
    return new UserNotificationInboxView(unreadCount, hasMore ? items.Take(take).ToList() : items, hasMore);
  }

  public async Task MarkAsReadAsync(Guid currentUserId, Guid notificationId, CancellationToken cancellationToken = default)
  {
    var notification = await _dbContext.UserNotifications
        .SingleOrDefaultAsync(
            candidate => candidate.Id == notificationId && candidate.UserId == currentUserId,
            cancellationToken)
        ?? throw new KeyNotFoundException("No encontramos la notificación.");

    if (notification.ReadAt is not null)
    {
      return;
    }

    notification.MarkAsRead(_timeProvider.GetUtcNow().UtcDateTime);
    await _dbContext.SaveChangesAsync(cancellationToken);
  }

  public async Task MarkAllAsReadAsync(Guid currentUserId, CancellationToken cancellationToken = default)
  {
    var unread = await _dbContext.UserNotifications
        .Where(notification => notification.UserId == currentUserId && notification.ReadAt == null)
        .ToListAsync(cancellationToken);

    if (unread.Count == 0)
    {
      return;
    }

    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
    foreach (var notification in unread)
    {
      notification.MarkAsRead(nowUtc);
    }

    await _dbContext.SaveChangesAsync(cancellationToken);
  }
}
