using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Finance.Admin;
using Barbershop.Application.Services.Admin;
using Barbershop.Application.Staff.Admin;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Finance;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "AdminReportsHttpIntegration")]
public sealed class AdminReportsHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";
  private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public AdminReportsHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task Admin_GetsSummaryReflectingSeededData()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAndPromoteAdminAsync(context, "rep-crud");
    AuthenticateAs(context.Client, admin.AccessToken);

    var staffId = await SeedStaffAsync(context.Factory, "rep-alex");
    await SeedIncomeAsync(context.Factory, staffId, 25000, Today, businessPercentage: 20);

    var first = new DateOnly(Today.Year, Today.Month, 1);
    var last = first.AddMonths(1).AddDays(-1);

    using var response = await context.Client.GetAsync(
        $"/api/v1/admin/reports/summary?from={first:yyyy-MM-dd}&to={last:yyyy-MM-dd}");
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var summary = await response.Content.ReadFromJsonAsync<ReportSummaryView>();
    Assert.NotNull(summary);
    Assert.True(summary!.TotalIncome >= 25000);
    Assert.Contains(summary.ByProfessional, p => p.StaffProfileId == staffId);
    Assert.Equal(5000, summary.BusinessIncome);
    Assert.Equal(20000, summary.ProfessionalIncome);
  }

  [Fact]
  public async Task GetSummary_FromAfterTo_ReturnsValidationProblem()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAndPromoteAdminAsync(context, "rep-badrange");
    AuthenticateAs(context.Client, admin.AccessToken);

    using var response = await context.Client.GetAsync(
        "/api/v1/admin/reports/summary?from=2026-06-30&to=2026-06-01");
    Assert.Equal((HttpStatusCode)422, response.StatusCode);
  }

  [Fact]
  public async Task GetSummary_AsPlainCustomer_ReturnsForbidden()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var customer = await RegisterCustomerAsync(context.Client, "rep-forbidden");
    AuthenticateAs(context.Client, customer.AccessToken);

    using var response = await context.Client.GetAsync("/api/v1/admin/reports/summary");
    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
  }

  [Fact]
  public async Task GetSummary_Unauthenticated_ReturnsUnauthorized()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    using var response = await context.Client.GetAsync("/api/v1/admin/reports/summary");
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  private static async Task<Guid> SeedStaffAsync(IntegrationTestFactory factory, string label)
  {
    using var scope = factory.Services.CreateScope();
    var staff = scope.ServiceProvider.GetRequiredService<IAdminStaffService>();
    var created = await staff.CreateAsync(new AdminStaffCreateRequest(
        FullName: $"Staff {label}",
        Email: UniqueEmail($"staff-{label}"),
        DisplayName: $"Staff {label}",
        InitialPassword: DefaultPassword,
        PhoneNumber: "+5730000000",
        Bio: null,
        DefaultAppointmentDurationMinutes: 30,
        PhotoMediaAssetId: null,
        TipsQrMediaAssetId: null,
        IsActive: true));
    return created.StaffProfileId;
  }

  private static async Task SeedIncomeAsync(IntegrationTestFactory factory, Guid staffProfileId, int amount, DateOnly occurredOn, int businessPercentage = 0)
  {
    using var scope = factory.Services.CreateScope();
    var income = scope.ServiceProvider.GetRequiredService<IAdminIncomeService>();
    // Income needs a service; create one first.
    var services = scope.ServiceProvider.GetRequiredService<IAdminServicesService>();
    var service = await services.CreateAsync(new ServiceCreateRequest("Corte", 25000, businessPercentage));
    await income.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(service.Id, staffProfileId, amount, false, occurredOn));
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
