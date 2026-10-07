using Barbershop.Infrastructure.Configuration;
using Barbershop.Infrastructure.Notifications;

namespace Barbershop.Tests.Features.Notifications;

public sealed class WebPushConfigurationTests
{
  [Theory]
  [InlineData("soporte@barbershop.local", true)]
  [InlineData("support@example.com", true)]
  [InlineData("admin@localhost", true)]
  [InlineData("https://localhost", true)]
  [InlineData("mailto:", true)]
  [InlineData("contacto@mibarberia.com", false)]
  [InlineData("mailto:contacto@mibarberia.com", false)]
  [InlineData("https://mibarberia.com", false)]
  public void GetConfigurationWarnings_FlagsPlaceholderContacts(string contact, bool expectWarning)
  {
    var options = new WebPushOptions
    {
      Enabled = true,
      PublicKey = "public",
      PrivateKey = "private",
      ContactEmail = contact,
    };

    Assert.Equal(expectWarning, WebPushNotificationSender.GetConfigurationWarnings(options).Count > 0);
  }

  [Fact]
  public void GetConfigurationWarnings_WhenDisabled_ReturnsNothing()
  {
    var options = new WebPushOptions { Enabled = false, ContactEmail = "soporte@barbershop.local" };

    Assert.Empty(WebPushNotificationSender.GetConfigurationWarnings(options));
  }

  [Theory]
  [InlineData("contacto@mibarberia.com", "mailto:contacto@mibarberia.com")]
  [InlineData("  contacto@mibarberia.com ", "mailto:contacto@mibarberia.com")]
  [InlineData("mailto:contacto@mibarberia.com", "mailto:contacto@mibarberia.com")]
  [InlineData("https://mibarberia.com", "https://mibarberia.com")]
  public void BuildSubject_AddsMailtoOnlyWhenNeeded(string contact, string expected)
      => Assert.Equal(expected, WebPushNotificationSender.BuildSubject(contact));
}
