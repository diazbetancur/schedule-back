using Barbershop.Infrastructure.Notifications;

namespace Barbershop.Tests.Features.Notifications;

/// <summary>BUG-106: only browser push services may receive requests from the server.</summary>
public sealed class PushEndpointPolicyTests
{
  [Theory]
  [InlineData("https://fcm.googleapis.com/fcm/send/abc")]
  [InlineData("https://updates.push.services.mozilla.com/wpush/v2/x")]
  [InlineData("https://wns2-par02p.notify.windows.com/w/?token=x")]
  [InlineData("https://web.push.apple.com/x")]
  [InlineData("https://fcm.googleapis.com./fcm/send/abc")]
  public void KnownPushServices_AreAllowed(string endpoint)
      => Assert.True(PushEndpointPolicy.IsAllowed(endpoint));

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("http://fcm.googleapis.com/fcm/send/abc")]
  [InlineData("https://127.0.0.1/x")]
  [InlineData("https://2130706433/x")]
  [InlineData("https://0x7f.0.0.1/x")]
  [InlineData("https://[::ffff:169.254.169.254]/x")]
  [InlineData("https://[fd00::1]/x")]
  [InlineData("https://172.16.0.1/x")]
  [InlineData("https://localhost./x")]
  [InlineData("https://push.services.mozilla.com/x")]
  [InlineData("https://notify.windows.com/x")]
  [InlineData("https://apple.com/x")]
  [InlineData("https://fcm.googleapis.com:444/x")]
  [InlineData("https://a:b@web.push.apple.com/x")]
  [InlineData("https://xn--fcm-googleapis-com.evil/x")]
  public void EverythingElse_IsRejected(string? endpoint)
      => Assert.False(PushEndpointPolicy.IsAllowed(endpoint));
}
