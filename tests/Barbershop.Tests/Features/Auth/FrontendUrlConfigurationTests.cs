using Barbershop.Application.Auth;
using Barbershop.Application.Email;
using Barbershop.Infrastructure;
using Barbershop.Infrastructure.Configuration;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Barbershop.Tests.Features.Auth;

/// <summary>
/// Launch with the own domain: an empty App:FrontendUrl sent the password reset with a relative link
/// («/auth/reset-password?token=…») that no mail client opens. Outside Development/Testing it is required and https,
/// validated on start; the link itself can never be relative.
/// </summary>
public sealed class FrontendUrlConfigurationTests
{
  [Theory]
  [InlineData("Production", "https://zona8.app", true)]
  [InlineData("Production", "", false)]
  [InlineData("Production", "/auth", false)]
  [InlineData("Production", "zona8.app", false)]
  [InlineData("Production", "http://zona8.app", false)]
  [InlineData("QA", "https://qa-zona-8.vercel.app", true)]
  [InlineData("QA", "", false)]
  [InlineData("Development", "", true)]
  [InlineData("Development", "http://localhost:4200", true)]
  [InlineData("Testing", "", true)]
  [InlineData("Testing", "/relative", false)]
  public void FrontendUrl_IsValidatedOnStart(string environment, string frontendUrl, bool valid)
  {
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:FrontendUrl"] = frontendUrl })
        .Build();
    using var provider = new ServiceCollection().AddAppOptions(configuration, new TestEnvironment(environment)).BuildServiceProvider();

    if (valid)
    {
      Assert.Equal(frontendUrl, provider.GetRequiredService<IOptions<AppOptions>>().Value.FrontendUrl);
    }
    else
    {
      var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AppOptions>>().Value);
      Assert.Contains(AppOptions.FrontendUrlRequiredMessage, error.Failures);
    }
  }

  [Theory]
  [InlineData("https://zona8.app", "https://zona8.app/auth/reset-password?token=ABC")]
  [InlineData("https://zona8.app/", "https://zona8.app/auth/reset-password?token=ABC")]
  [InlineData(" http://localhost:4200 ", "http://localhost:4200/auth/reset-password?token=ABC")]
  public void TheLink_IsAlwaysAbsolute(string frontendUrl, string expected)
      => Assert.Equal(expected, AppOptions.BuildFrontendLink(frontendUrl, "auth/reset-password?token=ABC"));

  [Theory]
  [InlineData("")]
  [InlineData(null)]
  [InlineData("/")]
  [InlineData("zona8.app")]
  public void ARelativeOrMissingBase_IsAConfigurationError(string? frontendUrl)
  {
    var error = Assert.Throws<InvalidOperationException>(() => AppOptions.BuildFrontendLink(frontendUrl, "auth/reset-password?token=ABC"));
    Assert.Equal(AppOptions.FrontendUrlRequiredMessage, error.Message);
  }

  [Fact]
  public async Task ThePasswordResetEmail_CarriesTheAbsoluteLink()
  {
    var (service, emails, db) = await CreateAsync("https://zona8.app");
    await service.ForgotPasswordAsync(new ForgotPasswordRequest("carla@example.com"));

    var email = Assert.Single(emails.Sent);
    Assert.Contains("https://zona8.app/auth/reset-password?token=", email.TextBody);
    Assert.DoesNotContain("\n/auth/reset-password", email.TextBody);
    await db.DisposeAsync();
  }

  [Fact]
  public async Task WithoutFrontendUrl_NoTokenIsStored_AndNoRelativeLinkIsSent()
  {
    var (service, emails, db) = await CreateAsync(string.Empty);

    await Assert.ThrowsAsync<InvalidOperationException>(() => service.ForgotPasswordAsync(new ForgotPasswordRequest("carla@example.com")));

    Assert.Empty(emails.Sent);
    Assert.Null((await db.Users.AsNoTracking().SingleAsync()).PasswordResetTokenHash);
    await db.DisposeAsync();
  }

  private static async Task<(AuthService Service, RecordingEmailSender Emails, AppDbContext Db)> CreateAsync(string frontendUrl)
  {
    var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
    var hasher = new PasswordHasher<object>();
    await new IdentitySeedService(db, hasher, Options.Create(new SeedAdminOptions()), new TestEnvironment("Testing"), TimeProvider.System)
        .EnsureSeededAsync();
    var emails = new RecordingEmailSender();
    var service = new AuthService(
        db,
        hasher,
        emails,
        Options.Create(new JwtOptions
        {
          Enabled = true,
          Issuer = "Barbershop.Tests",
          Audience = "Barbershop.Tests.Client",
          SigningKey = "12345678901234567890123456789012-auth-tests-key",
          AccessTokenMinutes = 60,
          RefreshTokenDays = 7,
          RequireHttpsMetadata = false
        }),
        Options.Create(new AppOptions { FrontendUrl = frontendUrl }),
        TimeProvider.System);
    await service.RegisterAsync(new RegisterRequest("Carla Cliente", "carla@example.com", "Secret123!", "3001234567"));
    return (service, emails, db);
  }

  private sealed class RecordingEmailSender : IEmailSender
  {
    public List<EmailMessage> Sent { get; } = [];

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
      Sent.Add(message);
      return Task.CompletedTask;
    }
  }

  private sealed class TestEnvironment(string name) : IHostEnvironment
  {
    public string EnvironmentName { get; set; } = name;
    public string ApplicationName { get; set; } = "Barbershop.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(AppContext.BaseDirectory);
  }
}
