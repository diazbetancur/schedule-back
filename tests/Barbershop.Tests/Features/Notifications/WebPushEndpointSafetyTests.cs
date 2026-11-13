using System.Net;
using System.Security.Cryptography;
using Barbershop.Infrastructure.Configuration;
using Barbershop.Infrastructure.Notifications;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebPush;
using DomainPushSubscription = Barbershop.Domain.Users.PushSubscription;

namespace Barbershop.Tests.Features.Notifications;

/// <summary>
/// BUG-106 (SSRF): a subscription could point the server's push requests to internal addresses, and
/// POST /notifications/test returned part of the target's response. The HTTP handler here records every request;
/// nothing leaves the process.
/// </summary>
public sealed class WebPushEndpointSafetyTests : IDisposable
{
  private const string ResponseSecret = "ami-id: i-0123 INTERNAL-SECRET";

  private readonly AppDbContext _dbContext;
  private readonly RecordingHandler _handler = new();
  private readonly WebPushNotificationSender _sender;
  private readonly Guid _userId = Guid.NewGuid();

  public WebPushEndpointSafetyTests()
  {
    _dbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);

    var vapid = VapidHelper.GenerateVapidKeys();
    _sender = new WebPushNotificationSender(
        _dbContext,
        Options.Create(new WebPushOptions
        {
          Enabled = true,
          PublicKey = vapid.PublicKey,
          PrivateKey = vapid.PrivateKey,
          ContactEmail = "contacto@zona8.co",
        }),
        new WebPushClient(new HttpClient(_handler)),
        NullLogger<WebPushNotificationSender>.Instance);
  }

  public void Dispose() => _dbContext.Dispose();

  [Theory]
  [InlineData("https://127.0.0.1:1/x")]
  [InlineData("https://169.254.169.254/latest/meta-data")]
  [InlineData("https://localhost/x")]
  [InlineData("https://[::1]/x")]
  [InlineData("https://10.0.0.1/x")]
  public async Task SendTest_ToASubscriptionSavedBeforeTheRule_NeverContactsItAndRemovesIt(string endpoint)
  {
    var internalTarget = await AddSubscriptionAsync(endpoint);

    var result = await _sender.SendTestAsync(_userId);

    Assert.Empty(_handler.RequestedHosts);
    var device = Assert.Single(result.Devices);
    Assert.Equal(internalTarget.Id, device.SubscriptionId);
    Assert.False(device.Delivered);
    Assert.True(device.Removed);
    Assert.Empty(await _dbContext.PushSubscriptions.ToListAsync());
  }

  [Fact]
  public async Task SendToUsers_SkipsInternalTargets_AndStillDeliversToRealPushServices()
  {
    await AddSubscriptionAsync("https://169.254.169.254/x");
    var real = await AddSubscriptionAsync("https://fcm.googleapis.com/fcm/send/device-1");

    await _sender.SendToUsersAsync([_userId], new Barbershop.Application.Notifications.PushNotificationMessage("Hola", "Texto", "/"));

    Assert.Equal(["fcm.googleapis.com"], _handler.RequestedHosts);
    Assert.Equal(real.Id, Assert.Single(await _dbContext.PushSubscriptions.ToListAsync()).Id);
  }

  [Theory]
  [InlineData(HttpStatusCode.BadRequest)]
  [InlineData(HttpStatusCode.Forbidden)]
  [InlineData(HttpStatusCode.InternalServerError)]
  public async Task SendTest_NeverReturnsTheBodyOfThePushServiceResponse(HttpStatusCode status)
  {
    _handler.Respond = () => new HttpResponseMessage(status) { Content = new StringContent(ResponseSecret) };
    await AddSubscriptionAsync("https://fcm.googleapis.com/fcm/send/device-1");

    var result = await _sender.SendTestAsync(_userId);

    var device = Assert.Single(result.Devices);
    Assert.False(device.Delivered);
    Assert.Equal((int)status, device.StatusCode);
    Assert.NotNull(device.Error);
    Assert.DoesNotContain("SECRET", device.Error);
    Assert.DoesNotContain("ami-id", device.Error);
  }

  [Fact]
  public async Task SendTest_ConnectionErrorsDoNotLeakNetworkDetails()
  {
    _handler.Respond = () => throw new HttpRequestException("Connection refused (10.1.2.3:8080)");
    await AddSubscriptionAsync("https://fcm.googleapis.com/fcm/send/device-1");

    var result = await _sender.SendTestAsync(_userId);

    var device = Assert.Single(result.Devices);
    Assert.DoesNotContain("10.1.2.3", device.Error);
  }

  private async Task<DomainPushSubscription> AddSubscriptionAsync(string endpoint)
  {
    using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    var point = ecdh.ExportParameters(false).Q;
    var publicKey = new byte[65];
    publicKey[0] = 0x04;
    point.X!.CopyTo(publicKey, 1);
    point.Y!.CopyTo(publicKey, 33);

    var subscription = new DomainPushSubscription(
        _userId,
        endpoint,
        Base64Url(publicKey),
        Base64Url(RandomNumberGenerator.GetBytes(16)),
        DateTime.UtcNow,
        "test-agent");
    _dbContext.PushSubscriptions.Add(subscription);
    await _dbContext.SaveChangesAsync();
    return subscription;
  }

  private static string Base64Url(byte[] bytes)
      => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

  private sealed class RecordingHandler : HttpMessageHandler
  {
    public List<string> RequestedHosts { get; } = [];

    public Func<HttpResponseMessage> Respond { get; set; } = () => new HttpResponseMessage(HttpStatusCode.Created);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      RequestedHosts.Add(request.RequestUri!.Host);
      return Task.FromResult(Respond());
    }
  }
}
