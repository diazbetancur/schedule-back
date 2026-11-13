using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Barbershop.Configuration;
using Barbershop.Application.Auth;
using Barbershop.Application.Notifications;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Notifications;

/// <summary>SONDA BUG-112 and BUG-110 over HTTP: per-user limits, repeated broadcasts and the read-only preview.</summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "NotificationRateLimitHttpIntegration")]
public sealed class NotificationRateLimitHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public NotificationRateLimitHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task TestNotification_IsLimitedPerSignedInUser_WithASpanishMessage()
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return;
    }

    using var factory = new IntegrationTestFactory(_postgres.ConnectionString);
    await factory.ResetDatabaseAsync();
    using var first = factory.CreateClient();
    using var second = factory.CreateClient();
    Authenticate(first, (await RegisterAsync(first, "Primera", UniqueEmail("first"))).AccessToken);
    Authenticate(second, (await RegisterAsync(second, "Segunda", UniqueEmail("second"))).AccessToken);

    for (var attempt = 1; attempt <= ApiFoundationServiceCollectionExtensions.NotificationTestPermitLimit; attempt++)
    {
      using var allowed = await first.PostAsync("/api/v1/notifications/test", null);
      Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
    }

    using var limited = await first.PostAsync("/api/v1/notifications/test", null);
    Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    Assert.StartsWith("Enviaste demasiadas notificaciones seguidas.", await ReadDetailAsync(limited));
    Assert.NotNull(limited.Headers.RetryAfter);

    // Another user (same client IP in the test host) keeps their own quota.
    using var otherUser = await second.PostAsync("/api/v1/notifications/test", null);
    Assert.NotEqual(HttpStatusCode.TooManyRequests, otherUser.StatusCode);
  }

  [Fact]
  public async Task Broadcast_PreviewCounts_RepeatIsRejected_AndTheLimitApplies()
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return;
    }

    using var factory = new IntegrationTestFactory(_postgres.ConnectionString);
    await factory.ResetDatabaseAsync();
    using var client = factory.CreateClient();
    var admin = await RegisterAdminAsync(client, factory);
    await SeedCustomerAsync(factory, "Cliente Uno");
    await SeedCustomerAsync(factory, "Cliente Dos");
    Authenticate(client, admin.AccessToken);

    var request = new NotificationBroadcastRequest("Promo", "Corte con descuento.", "all", null, null);

    using (var preview = await client.PostAsJsonAsync("/api/v1/admin/notifications/broadcast/preview", request))
    {
      Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
      var view = await preview.Content.ReadFromJsonAsync<NotificationBroadcastPreviewView>();
      // The two seeded customers plus the admin, who signed up as a customer too.
      Assert.Equal(new NotificationBroadcastPreviewView(3, "Todos los clientes"), view);
    }

    using (var sent = await client.PostAsJsonAsync("/api/v1/admin/notifications/broadcast", request))
    {
      Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
    }

    using (var repeated = await client.PostAsJsonAsync("/api/v1/admin/notifications/broadcast", request))
    {
      Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
      Assert.StartsWith("Ya se envió esta misma notificación", await ReadDetailAsync(repeated));
    }

    // The preview is not a broadcast: it neither counts against the limit nor stores campaigns.
    for (var index = 3; index <= ApiFoundationServiceCollectionExtensions.NotificationBroadcastPermitLimit; index++)
    {
      using var other = await client.PostAsJsonAsync(
          "/api/v1/admin/notifications/broadcast", request with { Body = $"Texto {index}" });
      Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    using var limited = await client.PostAsJsonAsync(
        "/api/v1/admin/notifications/broadcast", request with { Body = "Uno más" });
    Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    Assert.StartsWith("Enviaste demasiadas notificaciones seguidas.", await ReadDetailAsync(limited));

    using var scope = factory.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    Assert.Equal(ApiFoundationServiceCollectionExtensions.NotificationBroadcastPermitLimit - 1, await dbContext.NotificationCampaigns.CountAsync());
  }

  private static async Task<string> ReadDetailAsync(HttpResponseMessage response)
  {
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return document.RootElement.GetProperty("detail").GetString() ?? string.Empty;
  }

  private static async Task<AuthPayload> RegisterAsync(HttpClient client, string fullName, string email)
  {
    using var response = await client.PostAsJsonAsync(
        "/api/v1/auth/register", new RegisterRequest(fullName, email, DefaultPassword, "3001112233"));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<AuthPayload>())!;
  }

  private static async Task<AuthPayload> RegisterAdminAsync(HttpClient client, IntegrationTestFactory factory)
  {
    var email = UniqueEmail("admin");
    await RegisterAsync(client, "Admin Difusión", email);

    using (var scope = factory.Services.CreateScope())
    {
      var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var user = await dbContext.Users.Include(candidate => candidate.UserRoles)
          .SingleAsync(candidate => candidate.NormalizedEmail == email.ToUpperInvariant());
      var adminRole = await dbContext.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Admin.ToUpperInvariant());
      user.UserRoles.Add(new UserRole(user.Id, adminRole.Id, DateTime.UtcNow));
      await dbContext.SaveChangesAsync();
    }

    using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, DefaultPassword));
    Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    return (await login.Content.ReadFromJsonAsync<AuthPayload>())!;
  }

  private static async Task SeedCustomerAsync(IntegrationTestFactory factory, string fullName)
  {
    using var scope = factory.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var user = new User(fullName, UniqueEmail("customer"), "not-a-real-hash", DateTime.UtcNow);
    var customerRole = await dbContext.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Customer.ToUpperInvariant());
    user.UserRoles.Add(new UserRole(user.Id, customerRole.Id, DateTime.UtcNow));
    dbContext.Users.Add(user);
    await dbContext.SaveChangesAsync();
  }

  private static void Authenticate(HttpClient client, string accessToken)
      => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

  private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

  private sealed record AuthPayload(string AccessToken, AuthUserResponse User);
}
