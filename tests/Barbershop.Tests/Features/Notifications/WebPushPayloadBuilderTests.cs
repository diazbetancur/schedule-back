using System.Text.Json;
using Barbershop.Application.Notifications;
using Barbershop.Infrastructure.Notifications;

namespace Barbershop.Tests.Features.Notifications;

public sealed class WebPushPayloadBuilderTests
{
  [Fact]
  public void Build_IncludesOnActionClickSoTappingOpensTheApp()
  {
    var json = WebPushPayloadBuilder.Build(
        new PushNotificationMessage("Título", "Cuerpo", "/staff/appointments"),
        "/icons/icon.png",
        "/icons/badge.png");

    using var document = JsonDocument.Parse(json);
    var notification = document.RootElement.GetProperty("notification");
    var data = notification.GetProperty("data");
    var click = data.GetProperty("onActionClick").GetProperty("default");

    Assert.Equal("Título", notification.GetProperty("title").GetString());
    Assert.Equal("Cuerpo", notification.GetProperty("body").GetString());
    Assert.Equal("/icons/badge.png", notification.GetProperty("badge").GetString());
    Assert.Equal("/staff/appointments", data.GetProperty("url").GetString());
    Assert.Equal(WebPushPayloadBuilder.ClickOperation, click.GetProperty("operation").GetString());
    Assert.Equal("/staff/appointments", click.GetProperty("url").GetString());
  }

  [Fact]
  public void Build_WithTag_SetsTagAndRenotify()
  {
    var json = WebPushPayloadBuilder.Build(
        new PushNotificationMessage("T", "B", "/", "appointment-123"),
        "/i.png",
        "/b.png");

    using var document = JsonDocument.Parse(json);
    var notification = document.RootElement.GetProperty("notification");

    Assert.Equal("appointment-123", notification.GetProperty("tag").GetString());
    Assert.True(notification.GetProperty("renotify").GetBoolean());
  }

  [Fact]
  public void Build_WithoutTag_OmitsTagAndRenotify()
  {
    var json = WebPushPayloadBuilder.Build(new PushNotificationMessage("T", "B"), "/i.png", "/b.png");

    using var document = JsonDocument.Parse(json);
    var notification = document.RootElement.GetProperty("notification");

    Assert.False(notification.TryGetProperty("tag", out _));
    Assert.False(notification.TryGetProperty("renotify", out _));
    Assert.Equal("/", notification.GetProperty("data").GetProperty("url").GetString());
  }

  [Theory]
  [InlineData(null, "/")]
  [InlineData("", "/")]
  [InlineData("/customer/appointments", "/customer/appointments")]
  [InlineData("https://evil.example.com", "/")]
  [InlineData("//evil.example.com", "/")]
  [InlineData("/\\evil.example.com", "/")]
  [InlineData("customer/appointments", "/")]
  public void NormalizeUrl_OnlyAllowsAppRelativePaths(string? url, string expected)
      => Assert.Equal(expected, WebPushPayloadBuilder.NormalizeUrl(url));
}
