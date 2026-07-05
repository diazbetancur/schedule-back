using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Finance.Admin;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Finance;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "AdminFixedExpensesHttpIntegration")]
public sealed class AdminFixedExpensesHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public AdminFixedExpensesHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task Admin_CanRunFullFixedExpenseCrudLifecycle()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAndPromoteAdminAsync(context, "fx-crud");
    AuthenticateAs(context.Client, admin.AccessToken);

    using var create = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/fixed-expenses", new FixedExpenseCreateRequest("Arriendo", 1500000));
    Assert.Equal(HttpStatusCode.Created, create.StatusCode);
    var created = await create.Content.ReadFromJsonAsync<FixedExpenseView>();
    Assert.NotNull(created);
    Assert.Equal("Arriendo", created!.Name);
    Assert.Equal(1500000, created.DefaultAmount);

    using var list = await context.Client.GetAsync("/api/v1/admin/fixed-expenses");
    var items = await list.Content.ReadFromJsonAsync<List<FixedExpenseView>>();
    Assert.Contains(items!, e => e.Id == created.Id);

    using var update = await context.Client.PutAsJsonAsync(
        $"/api/v1/admin/fixed-expenses/{created.Id}", new FixedExpenseUpdateRequest("Arriendo local", 1600000));
    Assert.Equal(HttpStatusCode.OK, update.StatusCode);

    using var status = await context.Client.PatchAsJsonAsync(
        $"/api/v1/admin/fixed-expenses/{created.Id}/status", new FixedExpenseStatusUpdateRequest(false));
    var deactivated = await status.Content.ReadFromJsonAsync<FixedExpenseView>();
    Assert.False(deactivated!.IsActive);

    using var delete = await context.Client.DeleteAsync($"/api/v1/admin/fixed-expenses/{created.Id}");
    Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

    using var after = await context.Client.GetAsync("/api/v1/admin/fixed-expenses");
    var afterItems = await after.Content.ReadFromJsonAsync<List<FixedExpenseView>>();
    Assert.DoesNotContain(afterItems!, e => e.Id == created.Id);
  }

  [Fact]
  public async Task CreateFixedExpense_RejectsDuplicateName()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAndPromoteAdminAsync(context, "fx-dup");
    AuthenticateAs(context.Client, admin.AccessToken);

    using var first = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/fixed-expenses", new FixedExpenseCreateRequest("Luz", 200000));
    Assert.Equal(HttpStatusCode.Created, first.StatusCode);

    using var second = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/fixed-expenses", new FixedExpenseCreateRequest("luz", 210000));
    Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
  }

  [Fact]
  public async Task CreateFixedExpense_AsPlainCustomer_ReturnsForbidden()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var customer = await RegisterCustomerAsync(context.Client, "fx-forbidden");
    AuthenticateAs(context.Client, customer.AccessToken);

    using var response = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/fixed-expenses", new FixedExpenseCreateRequest("Agua", 80000));
    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
  }

  [Fact]
  public async Task ListFixedExpenses_Unauthenticated_ReturnsUnauthorized()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    using var response = await context.Client.GetAsync("/api/v1/admin/fixed-expenses");
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
