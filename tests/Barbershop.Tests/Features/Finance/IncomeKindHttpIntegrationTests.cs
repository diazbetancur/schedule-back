using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
[Trait("Category", "IncomeKindHttpIntegration")]
public sealed class IncomeKindHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";
  private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public IncomeKindHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task OlderClientJson_WithoutKind_StillCreatesAndEditsServiceIncome()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAndPromoteAdminAsync(context, "kind-legacy");
    AuthenticateAs(context.Client, admin.AccessToken);
    var serviceId = await SeedServiceAsync(context.Factory, "Corte", 25000, businessPercentage: 40);
    var staffId = await SeedStaffAsync(context.Factory, "kind-legacy");

    // Exactly the body the current production front end sends: no "kind" property at all.
    var legacyBody = $$"""{"serviceId":"{{serviceId}}","staffProfileId":"{{staffId}}","amount":20000,"isPromo":false,"occurredOn":"{{Today:yyyy-MM-dd}}"}""";
    using var created = await context.Client.PostAsync("/api/v1/admin/income", Json(legacyBody));
    Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    var entry = (await created.Content.ReadFromJsonAsync<IncomeEntryView>())!;
    Assert.Equal("Service", entry.Kind);
    Assert.Equal(8000, entry.BusinessAmount);
    Assert.Equal(12000, entry.ProfessionalAmount);

    var legacyEdit = $$"""{"serviceId":"{{serviceId}}","staffProfileId":"{{staffId}}","amount":30000,"isPromo":true,"occurredOn":"{{Today:yyyy-MM-dd}}"}""";
    using var edited = await context.Client.PutAsync($"/api/v1/admin/income/{entry.Id}", Json(legacyEdit));
    Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
    var editedEntry = (await edited.Content.ReadFromJsonAsync<IncomeEntryView>())!;
    Assert.Equal("Service", editedEntry.Kind);
    Assert.Equal(30000, editedEntry.Amount);
    Assert.True(editedEntry.IsPromo);
  }

  [Fact]
  public async Task Admin_RecordsAMembershipSale_ThatReportsKeepApartFromServices()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAndPromoteAdminAsync(context, "kind-sale");
    AuthenticateAs(context.Client, admin.AccessToken);
    var serviceId = await SeedServiceAsync(context.Factory, "Corte", 25000, businessPercentage: 40);
    var staffId = await SeedStaffAsync(context.Factory, "kind-sale");

    using (var service = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/income", new IncomeEntryCreateRequest(serviceId, staffId, 20000, false, Today)))
    {
      Assert.Equal(HttpStatusCode.Created, service.StatusCode);
    }

    var saleBody = $$"""{"kind":"MembershipSale","amount":250000,"isPromo":false,"occurredOn":"{{Today:yyyy-MM-dd}}"}""";
    using var created = await context.Client.PostAsync("/api/v1/admin/income", Json(saleBody));
    Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    var sale = (await created.Content.ReadFromJsonAsync<IncomeEntryView>())!;
    Assert.Equal("MembershipSale", sale.Kind);
    Assert.Null(sale.StaffProfileId);
    Assert.Equal(0, sale.ProfessionalAmount);

    using var withStaff = await context.Client.PostAsync("/api/v1/admin/income",
        Json($$"""{"kind":"MembershipSale","staffProfileId":"{{staffId}}","amount":1000,"isPromo":false,"occurredOn":"{{Today:yyyy-MM-dd}}"}"""));
    Assert.Equal(HttpStatusCode.UnprocessableEntity, withStaff.StatusCode);

    using var changeKind = await context.Client.PutAsJsonAsync(
        $"/api/v1/admin/income/{sale.Id}", new IncomeEntryUpdateRequest(serviceId, staffId, 250000, false, Today, "Service"));
    Assert.Equal(HttpStatusCode.UnprocessableEntity, changeKind.StatusCode);

    var day = await context.Client.GetFromJsonAsync<List<IncomeEntryView>>($"/api/v1/admin/income?date={Today:yyyy-MM-dd}");
    Assert.Equal(2, day!.Count);
    var forStaff = await context.Client.GetFromJsonAsync<List<IncomeEntryView>>($"/api/v1/admin/income?date={Today:yyyy-MM-dd}&staffProfileId={staffId}");
    Assert.Equal("Service", Assert.Single(forStaff!).Kind);

    var summary = await context.Client.GetFromJsonAsync<ReportSummaryView>($"/api/v1/admin/reports/summary?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
    Assert.Equal(270000, summary!.TotalIncome);
    Assert.Equal(1, summary.IncomeCount);
    Assert.Equal(250000, summary.MembershipSalesIncome);
    Assert.Equal(1, summary.MembershipSalesCount);
    Assert.Equal(12000, summary.ProfessionalIncome);
    Assert.Equal(8000 + 250000, summary.BusinessIncome);
    var row = Assert.Single(summary.ByProfessional);
    Assert.Equal(20000, row.IncomeTotal);
    Assert.Equal(1, row.IncomeCount);
  }

  private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

  private static async Task<Guid> SeedServiceAsync(IntegrationTestFactory factory, string name, int basePrice, int businessPercentage)
  {
    using var scope = factory.Services.CreateScope();
    var service = scope.ServiceProvider.GetRequiredService<IAdminServicesService>();
    return (await service.CreateAsync(new ServiceCreateRequest(name, basePrice, businessPercentage))).Id;
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

    using (var scope = context.Factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var user = await db.Users
          .Include(candidate => candidate.UserRoles)
          .SingleAsync(candidate => candidate.NormalizedEmail == email.ToUpperInvariant());
      var adminRole = await db.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Admin.ToUpperInvariant());
      if (user.UserRoles.All(assignment => assignment.RoleId != adminRole.Id))
      {
        user.UserRoles.Add(new UserRole(user.Id, adminRole.Id, DateTime.UtcNow));
      }
      await db.SaveChangesAsync();
    }

    using var login = await context.Client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, DefaultPassword));
    Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    var payload = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;
    return new AuthSession(payload.User.Id, payload.AccessToken);
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
