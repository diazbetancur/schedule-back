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

  /// <summary>BUG-106 (SSRF): the five endpoints SONDA registered, plus other internal or foreign targets.</summary>
  [Theory]
  [InlineData("https://127.0.0.1:1/x")]
  [InlineData("https://169.254.169.254/x")]
  [InlineData("https://localhost/x")]
  [InlineData("https://[::1]/x")]
  [InlineData("https://10.0.0.1/x")]
  [InlineData("https://192.168.1.10/fcm/send/x")]
  [InlineData("https://metadata.google.internal/x")]
  [InlineData("https://fcm.googleapis.com.evil.example/x")]
  [InlineData("https://evilfcm.googleapis.com.attacker.io/x")]
  [InlineData("https://notify.windows.com.attacker.io/x")]
  [InlineData("https://fcm.googleapis.com:8443/fcm/send/x")]
  [InlineData("https://user@fcm.googleapis.com/fcm/send/x")]
  public async Task SubscribeAsync_EndpointOutsideTheKnownPushServices_IsRejected(string endpoint)
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(
        () => _service.SubscribeAsync(Guid.NewGuid(), Request(endpoint, "p256", "auth")));

    Assert.True(exception.Errors.ContainsKey("endpoint"));
    Assert.Empty(_dbContext.PushSubscriptions);
  }

  [Theory]
  [InlineData("https://fcm.googleapis.com/fcm/send/abc:def")]
  [InlineData("https://updates.push.services.mozilla.com/wpush/v2/gAAAA")]
  [InlineData("https://wns2-bl2p.notify.windows.com/w/?token=BQYAAA")]
  [InlineData("https://web.push.apple.com/QGuQyavXutnMbz")]
  [InlineData("https://FCM.googleapis.com:443/fcm/send/upper")]
  public async Task SubscribeAsync_BrowserPushServices_AreAccepted(string endpoint)
  {
    await _service.SubscribeAsync(Guid.NewGuid(), Request(endpoint, "p256", "auth"));

    Assert.Single(_dbContext.PushSubscriptions);
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
