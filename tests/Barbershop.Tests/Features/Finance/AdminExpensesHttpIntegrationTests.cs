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
[Trait("Category", "AdminExpensesHttpIntegration")]
public sealed class AdminExpensesHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";
  private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public AdminExpensesHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task Admin_CanRegisterListAndDeleteExpense_LinkedToFixedExpense()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAndPromoteAdminAsync(context, "exp-crud");
    AuthenticateAs(context.Client, admin.AccessToken);

    var fixedExpenseId = await SeedFixedExpenseAsync(context.Factory, "Arriendo", 1500000);

    using var create = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/expenses",
        new ExpenseEntryCreateRequest(fixedExpenseId, "ignored", 1500000, Today));
    Assert.Equal(HttpStatusCode.Created, create.StatusCode);
    var created = await create.Content.ReadFromJsonAsync<ExpenseEntryView>();
    Assert.NotNull(created);
    Assert.Equal(fixedExpenseId, created!.FixedExpenseId);
    Assert.Equal("Arriendo", created.Name);
    Assert.Equal(1500000, created.Amount);

    using var list = await context.Client.GetAsync($"/api/v1/admin/expenses?year={Today.Year}&month={Today.Month}");
    Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    var items = await list.Content.ReadFromJsonAsync<List<ExpenseEntryView>>();
    Assert.Contains(items!, e => e.Id == created.Id);

    using var otherMonth = await context.Client.GetAsync($"/api/v1/admin/expenses?year={Today.AddMonths(-1).Year}&month={Today.AddMonths(-1).Month}");
    var otherItems = await otherMonth.Content.ReadFromJsonAsync<List<ExpenseEntryView>>();
    Assert.DoesNotContain(otherItems!, e => e.Id == created.Id);

    using var delete = await context.Client.DeleteAsync($"/api/v1/admin/expenses/{created.Id}");
    Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

    using var after = await context.Client.GetAsync($"/api/v1/admin/expenses?year={Today.Year}&month={Today.Month}");
    var afterItems = await after.Content.ReadFromJsonAsync<List<ExpenseEntryView>>();
    Assert.DoesNotContain(afterItems!, e => e.Id == created.Id);
  }

  [Fact]
  public async Task Admin_CanRegisterAdHocExpense()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAndPromoteAdminAsync(context, "exp-adhoc");
    AuthenticateAs(context.Client, admin.AccessToken);

    using var create = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/expenses",
        new ExpenseEntryCreateRequest(null, "Insumos varios", 30000, Today));
    Assert.Equal(HttpStatusCode.Created, create.StatusCode);
    var created = await create.Content.ReadFromJsonAsync<ExpenseEntryView>();
    Assert.Null(created!.FixedExpenseId);
    Assert.Equal("Insumos varios", created.Name);
  }

  [Fact]
  public async Task CreateExpense_AsPlainCustomer_ReturnsForbidden()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var customer = await RegisterCustomerAsync(context.Client, "exp-forbidden");
    AuthenticateAs(context.Client, customer.AccessToken);

    using var response = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/expenses",
        new ExpenseEntryCreateRequest(null, "Insumos", 10000, Today));
    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
  }

  [Fact]
  public async Task GetExpenses_Unauthenticated_ReturnsUnauthorized()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    using var response = await context.Client.GetAsync("/api/v1/admin/expenses");
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  private static async Task<Guid> SeedFixedExpenseAsync(IntegrationTestFactory factory, string name, int defaultAmount)
  {
    using var scope = factory.Services.CreateScope();
    var service = scope.ServiceProvider.GetRequiredService<IAdminFixedExpensesService>();
    var created = await service.CreateAsync(new FixedExpenseCreateRequest(name, defaultAmount));
    return created.Id;
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
