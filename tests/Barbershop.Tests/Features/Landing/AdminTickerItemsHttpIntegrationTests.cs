using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Landing;
using Barbershop.Application.PublicContent;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Landing;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "AdminTickerItemsHttpIntegration")]
public sealed class AdminTickerItemsHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public AdminTickerItemsHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task Admin_CanRunFullTickerItemCrudLifecycle()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAndPromoteAdminAsync(context, "crud");
    AuthenticateAs(context.Client, admin.AccessToken);

    using var createResponse = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/content/ticker-items", new CreateTickerItemRequest("El corte es oficio", 0, true));
    Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
    var created = await createResponse.Content.ReadFromJsonAsync<TickerItemResponse>();
    Assert.NotNull(created);
    Assert.Equal("El corte es oficio", created!.Text);
    Assert.True(created.IsActive);

    using var listResponse = await context.Client.GetAsync("/api/v1/admin/content/ticker-items");
    Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
    var list = await listResponse.Content.ReadFromJsonAsync<List<TickerItemResponse>>();
    Assert.Contains(list!, i => i.Id == created.Id);

    using var updateResponse = await context.Client.PutAsJsonAsync(
        $"/api/v1/admin/content/ticker-items/{created.Id}",
        new UpdateTickerItemRequest("Sin fila · Con cita", 1, false));
    Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
    var updated = await updateResponse.Content.ReadFromJsonAsync<TickerItemResponse>();
    Assert.Equal("Sin fila · Con cita", updated!.Text);
    Assert.False(updated.IsActive);

    using var deleteResponse = await context.Client.DeleteAsync($"/api/v1/admin/content/ticker-items/{created.Id}");
    Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

    using var listAfterDelete = await context.Client.GetAsync("/api/v1/admin/content/ticker-items");
    var listAfter = await listAfterDelete.Content.ReadFromJsonAsync<List<TickerItemResponse>>();
    Assert.DoesNotContain(listAfter!, i => i.Id == created.Id);
  }

  [Fact]
  public async Task GetPublicTickerItems_ReturnsOnlyActiveItems_NoAuthRequired()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAndPromoteAdminAsync(context, "public");
    AuthenticateAs(context.Client, admin.AccessToken);

    using var createActive = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/content/ticker-items", new CreateTickerItemRequest("Activa", 0, true));
    Assert.Equal(HttpStatusCode.Created, createActive.StatusCode);

    using var createInactive = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/content/ticker-items", new CreateTickerItemRequest("Inactiva", 1, false));
    Assert.Equal(HttpStatusCode.Created, createInactive.StatusCode);

    context.Client.DefaultRequestHeaders.Authorization = null;

    using var publicResponse = await context.Client.GetAsync("/api/v1/public/content/ticker-items");
    Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
    var publicList = await publicResponse.Content.ReadFromJsonAsync<List<TickerItemResponse>>();
    Assert.Single(publicList!);
    Assert.Equal("Activa", publicList![0].Text);
  }

  [Fact]
  public async Task CreateTickerItem_AsPlainCustomer_ReturnsForbidden()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var customer = await RegisterCustomerAsync(context.Client, "forbidden");
    AuthenticateAs(context.Client, customer.AccessToken);

    using var response = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/content/ticker-items", new CreateTickerItemRequest("No deberia crear", 0, true));
    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
  }

  [Fact]
  public async Task ListTickerItems_Unauthenticated_ReturnsUnauthorized()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    using var response = await context.Client.GetAsync("/api/v1/admin/content/ticker-items");
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  private async Task<AuthSession> RegisterAndPromoteAdminAsync(TestHttpContext context, string label)
  {
    var email = UniqueEmail($"admin-{label}");
    using (var register = await context.Client.PostAsJsonAsync(
        "/api/v1/auth/register", new RegisterRequest($"Admin {label}", email, DefaultPassword, null)))
    {
      Assert.Equal(HttpStatusCode.Created, register.StatusCode);
    }

    await WithScopeAsync(context.Factory, async serviceProvider =>
    {
      var db = serviceProvider.GetRequiredService<AppDbContext>();
      var user = await db.Users
          .Include(candidate => candidate.UserRoles)
          .SingleAsync(candidate => candidate.NormalizedEmail == email.ToUpperInvariant());
      var adminRole = await db.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Admin.ToUpperInvariant());
      if (user.UserRoles.All(assignment => assignment.RoleId != adminRole.Id))
      {
        user.UserRoles.Add(new UserRole(user.Id, adminRole.Id, DateTime.UtcNow));
      }
      await db.SaveChangesAsync();
    });

    using var login = await context.Client.PostAsJsonAsync(
        "/api/v1/auth/login", new LoginRequest(email, DefaultPassword));
    Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    var payload = await login.Content.ReadFromJsonAsync<TokenResponse>();
    Assert.NotNull(payload);
    return new AuthSession(payload!.User.Id, payload.AccessToken);
  }

  private static async Task<AuthSession> RegisterCustomerAsync(HttpClient client, string label)
  {
    using var response = await client.PostAsJsonAsync(
        "/api/v1/auth/register",
        new RegisterRequest($"Customer {label}", UniqueEmail($"customer-{label}"), DefaultPassword, null));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    var payload = await response.Content.ReadFromJsonAsync<TokenResponse>();
    Assert.NotNull(payload);
    return new AuthSession(payload!.User.Id, payload.AccessToken);
  }

  private async Task<TestHttpContext?> CreateContextAsync()
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return null;
    }

    var factory = new IntegrationTestFactory(_postgres.ConnectionString);
    await factory.ResetDatabaseAsync();
    return new TestHttpContext(factory, factory.CreateClient());
  }

  private static async Task WithScopeAsync(IntegrationTestFactory factory, Func<IServiceProvider, Task> action)
  {
    using var scope = factory.Services.CreateScope();
    await action(scope.ServiceProvider);
  }

  private static void AuthenticateAs(HttpClient client, string accessToken)
      => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

  private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

  private sealed record AuthSession(Guid UserId, string AccessToken);

  private sealed class TestHttpContext : IDisposable
  {
    public TestHttpContext(IntegrationTestFactory factory, HttpClient client)
    {
      Factory = factory;
      Client = client;
    }

    public IntegrationTestFactory Factory { get; }
    public HttpClient Client { get; }

    public void Dispose()
    {
      Client.Dispose();
      Factory.Dispose();
    }
  }
}
