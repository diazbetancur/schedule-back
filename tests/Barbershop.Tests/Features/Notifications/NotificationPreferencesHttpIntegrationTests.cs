using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Notifications;
using Barbershop.Tests.Infrastructure;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Notifications;

/// <summary>ADR-0019 (SONDA BUG-116) over HTTP: history and push preferences of the signed-in user.</summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "NotificationPreferencesHttpIntegration")]
public sealed class NotificationPreferencesHttpIntegrationTests
{
  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public NotificationPreferencesHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task HistoryAndPreferences_RequireSignIn_StartAllOn_AndSaveOnlyOptionalCategories()
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return;
    }

    using var factory = new IntegrationTestFactory(_postgres.ConnectionString);
    await factory.ResetDatabaseAsync();
    using var client = factory.CreateClient();

    using (var anonymous = await client.GetAsync("/api/v1/notifications/preferences"))
    {
      Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    using (var register = await client.PostAsJsonAsync(
        "/api/v1/auth/register",
        new RegisterRequest("Carla Cliente", $"carla-{Guid.NewGuid():N}@example.com", "Secret123!", "3001112233")))
    {
      Assert.Equal(HttpStatusCode.Created, register.StatusCode);
      var payload = (await register.Content.ReadFromJsonAsync<AuthPayload>())!;
      client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", payload.AccessToken);
    }

    var defaults = await client.GetFromJsonAsync<NotificationPreferencesView>("/api/v1/notifications/preferences");
    Assert.All(defaults!.Categories, item => Assert.True(item.PushEnabled));
    Assert.Equal(["appointments", "memberships", "loyalty", "purchases", "campaigns"], defaults.Categories.Select(item => item.Category));

    using (var saved = await client.PutAsJsonAsync(
        "/api/v1/notifications/preferences",
        new NotificationPreferencesUpdateRequest([new("campaigns", false)])))
    {
      Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
      var view = (await saved.Content.ReadFromJsonAsync<NotificationPreferencesView>())!;
      Assert.False(view.Categories.Single(item => item.Category == "campaigns").PushEnabled);
    }

    using (var account = await client.PutAsJsonAsync(
        "/api/v1/notifications/preferences",
        new NotificationPreferencesUpdateRequest([new("account", false)])))
    {
      Assert.Equal(HttpStatusCode.UnprocessableEntity, account.StatusCode);
    }

    var history = await client.GetFromJsonAsync<UserNotificationHistoryView>("/api/v1/notifications/history?page=1&pageSize=20&unreadOnly=true");
    Assert.Equal((1, 20), (history!.Page, history.PageSize));

    using var tooLarge = await client.GetAsync("/api/v1/notifications/history?pageSize=500");
    Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLarge.StatusCode);
  }

  private sealed record AuthPayload(string AccessToken, AuthUserResponse User);
}
