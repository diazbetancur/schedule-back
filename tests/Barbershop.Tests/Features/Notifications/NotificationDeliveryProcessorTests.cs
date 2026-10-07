using Barbershop.Application.Notifications;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Notifications;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbershop.Tests.Features.Notifications;

public sealed class NotificationDeliveryProcessorTests : IDisposable
{
  private static readonly DateTimeOffset Start = new(2026, 10, 7, 14, 0, 0, TimeSpan.Zero);

  private readonly AppDbContext _dbContext;
  private readonly ManualTimeProvider _time = new(Start);
  private readonly FakePushDeliveryChannel _channel = new();
  private readonly NotificationDeliveryProcessor _processor;

  public NotificationDeliveryProcessorTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    _processor = new NotificationDeliveryProcessor(_dbContext, _channel, _time, NullLogger<NotificationDeliveryProcessor>.Instance);
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task ProcessDueAsync_Delivered_MarksSentAndPassesRemainingTimeAsTtl()
  {
    var delivery = await QueueAsync(expiresIn: TimeSpan.FromHours(3));

    var processed = await _processor.ProcessDueAsync(10);

    Assert.Equal(1, processed);
    Assert.Equal(NotificationDeliveryStatus.Sent, delivery.Status);
    Assert.Equal(1, delivery.Attempts);
    Assert.Equal(Start.UtcDateTime, delivery.CompletedAt);

    var call = Assert.Single(_channel.Calls);
    Assert.Equal(delivery.UserId, call.UserId);
    Assert.Equal("Título", call.Message.Title);
    Assert.Equal(TimeSpan.FromHours(3), call.Message.TimeToLive);
  }

  [Fact]
  public async Task ProcessDueAsync_TransientFailure_RetriesLaterWithoutResendingToDevicesThatGotIt()
  {
    var deliveredDevice = Guid.NewGuid();
    _channel.Respond = (_, _) => new PushChannelResult(true, 2, [deliveredDevice], 1, 0, "503 Service Unavailable");
    var delivery = await QueueAsync();

    await _processor.ProcessDueAsync(10);

    Assert.Equal(NotificationDeliveryStatus.Pending, delivery.Status);
    Assert.Equal(1, delivery.Attempts);
    Assert.Equal(Start.UtcDateTime.AddSeconds(30), delivery.NextAttemptAt);
    Assert.Equal("503 Service Unavailable", delivery.LastError);

    // Not due yet.
    Assert.Equal(0, await _processor.ProcessDueAsync(10));

    _time.Advance(TimeSpan.FromSeconds(30));
    _channel.Respond = (_, _) => new PushChannelResult(true, 1, [Guid.NewGuid()], 0, 0, null);

    await _processor.ProcessDueAsync(10);

    Assert.Equal(NotificationDeliveryStatus.Sent, delivery.Status);
    Assert.Equal(2, delivery.Attempts);
    Assert.Contains(deliveredDevice, _channel.Calls[1].Skipped);
  }

  [Fact]
  public async Task ProcessDueAsync_KeepsFailing_GivesUpAfterMaxAttempts()
  {
    _channel.Respond = (_, _) => new PushChannelResult(true, 1, [], 1, 0, "timeout");
    var delivery = await QueueAsync(expiresIn: TimeSpan.FromDays(1));

    for (var attempt = 0; attempt < NotificationDelivery.MaxAttempts; attempt++)
    {
      await _processor.ProcessDueAsync(10);
      _time.Advance(TimeSpan.FromHours(1));
    }

    Assert.Equal(NotificationDeliveryStatus.Failed, delivery.Status);
    Assert.Equal(NotificationDelivery.MaxAttempts, delivery.Attempts);
    Assert.Equal(NotificationDelivery.MaxAttempts, _channel.Calls.Count);
  }

  [Fact]
  public async Task ProcessDueAsync_PermanentFailureOnEveryDevice_MarksFailedWithoutRetry()
  {
    _channel.Respond = (_, _) => new PushChannelResult(true, 1, [], 0, 1, "403 BadJwtToken");
    var delivery = await QueueAsync();

    await _processor.ProcessDueAsync(10);

    Assert.Equal(NotificationDeliveryStatus.Failed, delivery.Status);
    Assert.Equal("403 BadJwtToken", delivery.LastError);
  }

  [Fact]
  public async Task ProcessDueAsync_Expired_IsSkippedWithoutSending()
  {
    var delivery = await QueueAsync(expiresIn: TimeSpan.FromMinutes(5));
    _time.Advance(TimeSpan.FromMinutes(10));

    await _processor.ProcessDueAsync(10);

    Assert.Equal(NotificationDeliveryStatus.Skipped, delivery.Status);
    Assert.Empty(_channel.Calls);
  }

  [Fact]
  public async Task ProcessDueAsync_UserWithoutDevicesOrPushDisabled_IsSkipped()
  {
    _channel.Respond = (_, _) => new PushChannelResult(true, 0, [], 0, 0, null);
    var noDevices = await QueueAsync();
    await _processor.ProcessDueAsync(10);

    _channel.Respond = (_, _) => new PushChannelResult(false, 0, [], 0, 0, null);
    var disabled = await QueueAsync();
    await _processor.ProcessDueAsync(10);

    Assert.Equal(NotificationDeliveryStatus.Skipped, noDevices.Status);
    Assert.Equal(NotificationDeliveryStatus.Skipped, disabled.Status);
  }

  private async Task<NotificationDelivery> QueueAsync(TimeSpan? expiresIn = null)
  {
    var now = _time.GetUtcNow().UtcDateTime;
    var delivery = new NotificationDelivery(
        Guid.NewGuid(),
        null,
        NotificationDeliveryChannel.Push,
        "Título",
        "Cuerpo",
        "/customer/appointments",
        "appointment-1",
        isTimeSensitive: true,
        now,
        now + (expiresIn ?? TimeSpan.FromHours(24)));

    _dbContext.NotificationDeliveries.Add(delivery);
    await _dbContext.SaveChangesAsync();
    return delivery;
  }
}
