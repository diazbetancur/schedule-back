using Barbershop.Application.Notifications;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Notifications;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbershop.Tests.Features.Notifications;

public sealed class NotificationDispatcherTests : IDisposable
{
  private static readonly DateTimeOffset Now = new(2026, 10, 7, 14, 0, 0, TimeSpan.Zero);

  private readonly AppDbContext _dbContext;
  private readonly NotificationWorkerSignal _signal = new();
  private readonly NotificationDispatcher _dispatcher;

  public NotificationDispatcherTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    _dispatcher = new NotificationDispatcher(_dbContext, new ManualTimeProvider(Now), _signal, NullLogger<NotificationDispatcher>.Instance);
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task DispatchAsync_SavesInboxEntryAndQueuesPushForEachRecipient()
  {
    var first = Guid.NewGuid();
    var second = Guid.NewGuid();

    await _dispatcher.DispatchAsync(
        [first, second, first],
        UserNotificationTypes.AppointmentCreated,
        new PushNotificationMessage("Nueva cita agendada", "Ana agendó una cita.", "/staff/appointments", "appointment-1"));

    var inbox = await _dbContext.UserNotifications.ToListAsync();
    var deliveries = await _dbContext.NotificationDeliveries.ToListAsync();

    Assert.Equal(2, inbox.Count);
    Assert.Equal(2, deliveries.Count);
    Assert.All(inbox, entry => Assert.Equal(UserNotificationTypes.AppointmentCreated, entry.Type));
    Assert.All(deliveries, delivery =>
    {
      Assert.Equal(NotificationDeliveryStatus.Pending, delivery.Status);
      Assert.Equal(NotificationDeliveryChannel.Push, delivery.Channel);
      Assert.Equal("appointment-1", delivery.Tag);
      Assert.Equal("/staff/appointments", delivery.Url);
      Assert.Equal(Now.UtcDateTime, delivery.NextAttemptAt);
      Assert.Equal(Now.UtcDateTime.AddHours(24), delivery.ExpiresAt);
      Assert.Contains(inbox, entry => entry.Id == delivery.UserNotificationId && entry.UserId == delivery.UserId);
    });

    // The worker is woken up right away.
    Assert.True(await _signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
  }

  [Fact]
  public async Task DispatchAsync_WithDedupKey_CreatesItOnlyOncePerUser()
  {
    var userId = Guid.NewGuid();
    var otherUserId = Guid.NewGuid();
    var options = new NotificationDispatchOptions("reminder-24h:abc", Now.UtcDateTime.AddHours(5));
    var message = new PushNotificationMessage("Recordatorio de tu cita", "Tienes cita mañana.");

    var firstRun = await _dispatcher.DispatchAsync([userId], UserNotificationTypes.AppointmentReminder, message, options);
    var secondRun = await _dispatcher.DispatchAsync([userId], UserNotificationTypes.AppointmentReminder, message, options);
    var otherUser = await _dispatcher.DispatchAsync([userId, otherUserId], UserNotificationTypes.AppointmentReminder, message, options);

    Assert.Equal(1, firstRun);
    Assert.Equal(0, secondRun);
    Assert.Equal(1, otherUser);
    Assert.Equal(2, await _dbContext.UserNotifications.CountAsync());
    Assert.Equal(2, await _dbContext.NotificationDeliveries.CountAsync());
    Assert.All(await _dbContext.NotificationDeliveries.ToListAsync(), delivery => Assert.Equal(Now.UtcDateTime.AddHours(5), delivery.ExpiresAt));
  }

  [Fact]
  public async Task DispatchAsync_OnlyStoresAppRelativeUrls()
  {
    await _dispatcher.DispatchAsync(
        [Guid.NewGuid()],
        UserNotificationTypes.Campaign,
        new PushNotificationMessage("Promo", "Texto", "https://evil.example.com"));

    Assert.Equal("/", (await _dbContext.UserNotifications.SingleAsync()).Url);
  }

  [Fact]
  public async Task DispatchAsync_NoRecipients_DoesNothing()
  {
    var created = await _dispatcher.DispatchAsync([], UserNotificationTypes.Campaign, new PushNotificationMessage("T", "B"), new NotificationDispatchOptions());

    Assert.Equal(0, created);
    Assert.Empty(_dbContext.NotificationDeliveries);
    Assert.False(await _signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
  }
}
