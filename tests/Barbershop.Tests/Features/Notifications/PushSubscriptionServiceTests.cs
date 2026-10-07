using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Notifications;
using Barbershop.Infrastructure.Notifications;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Features.Notifications;

public sealed class PushSubscriptionServiceTests : IDisposable
{
  private const string Endpoint = "https://fcm.googleapis.com/fcm/send/device-1";

  private readonly AppDbContext _dbContext;
  private readonly PushSubscriptionService _service;

  public PushSubscriptionServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    _service = new PushSubscriptionService(_dbContext, TimeProvider.System);
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task SubscribeAsync_NewEndpoint_CreatesSubscription()
  {
    var userId = Guid.NewGuid();

    await _service.SubscribeAsync(userId, Request(Endpoint, "p256-a", "auth-a"));

    var subscription = Assert.Single(_dbContext.PushSubscriptions);
    Assert.Equal(userId, subscription.UserId);
    Assert.Equal(Endpoint, subscription.Endpoint);
  }

  [Fact]
  public async Task SubscribeAsync_SameEndpointDifferentUser_RebindsDeviceToNewUser()
  {
    var firstUser = Guid.NewGuid();
    var secondUser = Guid.NewGuid();

    await _service.SubscribeAsync(firstUser, Request(Endpoint, "p256-a", "auth-a"));
    await _service.SubscribeAsync(secondUser, Request(Endpoint, "p256-b", "auth-b"));

    var subscription = Assert.Single(_dbContext.PushSubscriptions);
    Assert.Equal(secondUser, subscription.UserId);
    Assert.Equal("p256-b", subscription.P256dhKey);
    Assert.Equal("auth-b", subscription.AuthKey);
  }

  [Fact]
  public async Task SubscribeAsync_SameEndpointSameUser_RefreshesRotatedKeys()
  {
    var userId = Guid.NewGuid();

    await _service.SubscribeAsync(userId, Request(Endpoint, "p256-old", "auth-old"));
    await _service.SubscribeAsync(userId, Request(Endpoint, "p256-new", "auth-new"));

    var subscription = Assert.Single(_dbContext.PushSubscriptions);
    Assert.Equal("p256-new", subscription.P256dhKey);
    Assert.Equal("auth-new", subscription.AuthKey);
  }

  [Fact]
  public async Task SubscribeAsync_LongUserAgent_IsTruncatedInsteadOfFailing()
  {
    var longUserAgent = new string('x', 600);

    await _service.SubscribeAsync(Guid.NewGuid(), Request(Endpoint, "p256", "auth", longUserAgent));

    var subscription = Assert.Single(_dbContext.PushSubscriptions);
    Assert.Equal(PushSubscriptionService.MaxUserAgentLength, subscription.UserAgent!.Length);
  }

  [Theory]
  [InlineData("")]
  [InlineData("not-a-url")]
  [InlineData("http://fcm.googleapis.com/fcm/send/device-1")]
  public async Task SubscribeAsync_InvalidEndpoint_ThrowsValidationProblem(string endpoint)
  {
    await Assert.ThrowsAsync<ValidationProblemException>(
        () => _service.SubscribeAsync(Guid.NewGuid(), Request(endpoint, "p256", "auth")));

    Assert.Empty(_dbContext.PushSubscriptions);
  }

  [Fact]
  public async Task UnsubscribeAsync_OnlyRemovesTheCallersOwnDevice()
  {
    var owner = Guid.NewGuid();
    await _service.SubscribeAsync(owner, Request(Endpoint, "p256", "auth"));

    await _service.UnsubscribeAsync(Guid.NewGuid(), new PushUnsubscribeRequest(Endpoint));
    Assert.Single(_dbContext.PushSubscriptions);

    await _service.UnsubscribeAsync(owner, new PushUnsubscribeRequest(Endpoint));
    Assert.Empty(_dbContext.PushSubscriptions);
  }

  private static PushSubscriptionRequest Request(string endpoint, string p256dh, string auth, string? userAgent = null)
      => new(endpoint, p256dh, auth, userAgent);
}
