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
[Trait("Category", "AdminIncomeHttpIntegration")]
public sealed class AdminIncomeHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";
  private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public AdminIncomeHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task Admin_CanRegisterListFilterAndDeleteIncome()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAndPromoteAdminAsync(context, "income-crud");
    AuthenticateAs(context.Client, admin.AccessToken);

    var serviceId = await SeedServiceAsync(context.Factory, "Corte", 25000, businessPercentage: 40);
    var staffId = await SeedStaffAsync(context.Factory, "income-alex");

    using var createResponse = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/income",
        new IncomeEntryCreateRequest(serviceId, staffId, 20000, IsPromo: true, Today));
    Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
    var created = await createResponse.Content.ReadFromJsonAsync<IncomeEntryView>();
    Assert.NotNull(created);
    Assert.Equal(20000, created!.Amount);
    Assert.Equal("Corte", created.ServiceName);
    Assert.Equal(25000, created.BasePrice);
    Assert.True(created.IsPromo);
    Assert.Equal(40, created.BusinessPercentage);
    Assert.Equal(8000, created.BusinessAmount);
    Assert.Equal(12000, created.ProfessionalAmount);

    using var listResponse = await context.Client.GetAsync($"/api/v1/admin/income?date={Today:yyyy-MM-dd}");
    Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
    var list = await listResponse.Content.ReadFromJsonAsync<List<IncomeEntryView>>();
    Assert.Contains(list!, e => e.Id == created.Id);

    using var otherDay = await context.Client.GetAsync($"/api/v1/admin/income?date={Today.AddDays(-1):yyyy-MM-dd}");
    var otherList = await otherDay.Content.ReadFromJsonAsync<List<IncomeEntryView>>();
    Assert.DoesNotContain(otherList!, e => e.Id == created.Id);

    using var byStaff = await context.Client.GetAsync($"/api/v1/admin/income?date={Today:yyyy-MM-dd}&staffProfileId={staffId}");
    var byStaffList = await byStaff.Content.ReadFromJsonAsync<List<IncomeEntryView>>();
    Assert.Contains(byStaffList!, e => e.Id == created.Id);

    using var deleteResponse = await context.Client.DeleteAsync($"/api/v1/admin/income/{created.Id}");
    Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

    using var afterDelete = await context.Client.GetAsync($"/api/v1/admin/income?date={Today:yyyy-MM-dd}");
    var afterList = await afterDelete.Content.ReadFromJsonAsync<List<IncomeEntryView>>();
    Assert.DoesNotContain(afterList!, e => e.Id == created.Id);
  }

  [Fact]
  public async Task CreateIncome_AsPlainCustomer_ReturnsForbidden()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var customer = await RegisterCustomerAsync(context.Client, "income-forbidden");
    AuthenticateAs(context.Client, customer.AccessToken);

    using var response = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/income",
        new IncomeEntryCreateRequest(Guid.NewGuid(), Guid.NewGuid(), 10000, false, Today));
    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
  }

  [Fact]
  public async Task GetIncome_Unauthenticated_ReturnsUnauthorized()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    using var response = await context.Client.GetAsync("/api/v1/admin/income");
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  private static async Task<Guid> SeedServiceAsync(IntegrationTestFactory factory, string name, int basePrice, int businessPercentage = 0)
  {
    using var scope = factory.Services.CreateScope();
    var service = scope.ServiceProvider.GetRequiredService<IAdminServicesService>();
    var created = await service.CreateAsync(new ServiceCreateRequest(name, basePrice, businessPercentage));
    return created.Id;
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
