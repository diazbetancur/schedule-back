using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Notifications;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Features.Notifications;

public sealed class UserNotificationsServiceTests : IDisposable
{
  private static readonly DateTime BaseTime = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

  private readonly AppDbContext _dbContext;
  private readonly UserNotificationsService _service;

  public UserNotificationsServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    _service = new UserNotificationsService(_dbContext, TimeProvider.System);
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task GetInboxAsync_ReturnsLatestFirstLimitedAndCountsAllUnread()
  {
    var userId = Guid.NewGuid();
    await SeedAsync(userId, count: 7);

    var inbox = await _service.GetInboxAsync(userId, 5);

    Assert.Equal(7, inbox.UnreadCount);
    Assert.Equal(5, inbox.Items.Count);
    Assert.Equal("Aviso 7", inbox.Items[0].Title);
    Assert.Equal("Aviso 3", inbox.Items[4].Title);
  }

  [Fact]
  public async Task GetInboxAsync_OnlyReturnsTheCurrentUsersNotifications()
  {
    var userId = Guid.NewGuid();
    await SeedAsync(userId, count: 2);
    await SeedAsync(Guid.NewGuid(), count: 3);

    var inbox = await _service.GetInboxAsync(userId, 5);

    Assert.Equal(2, inbox.UnreadCount);
    Assert.Equal(2, inbox.Items.Count);
  }

  [Fact]
  public async Task MarkAsReadAsync_MarksOnlyThatNotification()
  {
    var userId = Guid.NewGuid();
    var notifications = await SeedAsync(userId, count: 3);

    await _service.MarkAsReadAsync(userId, notifications[0].Id);

    var inbox = await _service.GetInboxAsync(userId, 5);
    Assert.Equal(2, inbox.UnreadCount);
    Assert.NotNull(inbox.Items.Single(item => item.Id == notifications[0].Id).ReadAtUtc);
  }

  [Fact]
  public async Task MarkAsReadAsync_AnotherUsersNotification_ThrowsNotFound()
  {
    var owner = Guid.NewGuid();
    var notifications = await SeedAsync(owner, count: 1);

    await Assert.ThrowsAsync<KeyNotFoundException>(
        () => _service.MarkAsReadAsync(Guid.NewGuid(), notifications[0].Id));

    Assert.Null((await _dbContext.UserNotifications.SingleAsync()).ReadAt);
  }

  [Fact]
  public async Task MarkAllAsReadAsync_ClearsTheUnreadCount()
  {
    var userId = Guid.NewGuid();
    var otherUserId = Guid.NewGuid();
    await SeedAsync(userId, count: 4);
    await SeedAsync(otherUserId, count: 2);

    await _service.MarkAllAsReadAsync(userId);

    Assert.Equal(0, (await _service.GetInboxAsync(userId, 5)).UnreadCount);
    Assert.Equal(2, (await _service.GetInboxAsync(otherUserId, 5)).UnreadCount);
  }

  private async Task<List<UserNotification>> SeedAsync(Guid userId, int count)
  {
    var notifications = Enumerable.Range(1, count)
        .Select(index => new UserNotification(
            userId,
            "appointment.created",
            $"Aviso {index}",
            $"Cuerpo {index}",
            "/staff/appointments",
            BaseTime.AddMinutes(index)))
        .ToList();

    _dbContext.UserNotifications.AddRange(notifications);
    await _dbContext.SaveChangesAsync();
    return notifications;
  }
}
