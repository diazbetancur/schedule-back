using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Customers.Claims;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Application.Memberships.Portal;
using Barbershop.Domain.Common;
using Barbershop.Domain.Services;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Customers;

/// <summary>
/// Own profiles and claims over real HTTP, JWT and PostgreSQL: what each side can reach, concurrent approvals and
/// creations, and the critical customers.claims.approve permission revoked with the same token.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "CustomerClaimsHttpIntegration")]
public sealed class CustomerClaimsHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";
  private const string OwnUrl = "/api/v1/customer/customer-profile";
  private const string AdminUrl = "/api/v1/admin/customer-claims";
  private const string Terms = "La membresía es personal e intransferible.";

  private readonly DateOnly _today = DateOnly.FromDateTime(BogotaClock.ToLocal(DateTime.UtcNow));
  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public CustomerClaimsHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task AClaimedHistoricalProfile_ShowsItsMemberships_OnlyAfterApproval_AndTheCustomerReachesNothingElse()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context, "flow");
    var (historicalId, membershipId) = await HistoricalCustomerWithMembershipAsync(context, admin.AccessToken);
    var customer = await RegisterAsync(context.Client, "Carla Cliente", UniqueEmail("carla"));

    AuthenticateAs(context.Client, customer.AccessToken);
    var before = (await context.Client.GetFromJsonAsync<OwnCustomerStatusView>(OwnUrl))!;
    Assert.Null(before.Profile);
    using (var submit = await context.Client.PostAsJsonAsync($"{OwnUrl}/claims",
        new CustomerClaimCreateRequest("Carla Cliente", "300 123 4567", customer.User.Email, null, Guid.NewGuid())))
    {
      Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
      var body = await submit.Content.ReadAsStringAsync();
      Assert.DoesNotContain(historicalId.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    Assert.Empty((await context.Client.GetPageItemsAsync<MyMembershipSummaryView>("/api/v1/customer/memberships"))!);
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(context.Client, HttpMethod.Get, AdminUrl));
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(context.Client, HttpMethod.Get, "/api/v1/admin/customers?q=Carla"));

    AuthenticateAs(context.Client, admin.AccessToken);
    var claim = Assert.Single((await context.Client.GetPageItemsAsync<CustomerClaimView>(AdminUrl)));
    var candidates = (await context.Client.GetFromJsonAsync<List<CustomerClaimCandidate>>($"{AdminUrl}/{claim.Id}/candidates"))!;
    Assert.Contains(candidates, candidate => candidate.CustomerId == historicalId && candidate.MatchedOn.Contains("phone"));

    using (var unverified = await context.Client.PostAsJsonAsync($"{AdminUrl}/{claim.Id}/approve",
        new CustomerClaimApproveRequest(claim.Version, historicalId, false, null)))
    {
      Assert.Equal(HttpStatusCode.UnprocessableEntity, unverified.StatusCode);
    }

    using (var approve = await context.Client.PostAsJsonAsync($"{AdminUrl}/{claim.Id}/approve",
        new CustomerClaimApproveRequest(claim.Version, historicalId, true, "Cédula verificada")))
    {
      Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
    }

    AuthenticateAs(context.Client, customer.AccessToken);
    var after = (await context.Client.GetFromJsonAsync<OwnCustomerStatusView>(OwnUrl))!;
    Assert.Equal(historicalId, after.Profile!.Id);
    var mine = (await context.Client.GetPageItemsAsync<MyMembershipSummaryView>("/api/v1/customer/memberships"))!;
    Assert.Equal([membershipId], mine.Select(item => item.Id));
  }

  [Fact]
  public async Task ConcurrentApprovalsOfTheSameProfile_LinkOnlyOneAccount_AndConcurrentOwnProfilesCreateOne()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context, "race");
    var (historicalId, _) = await HistoricalCustomerWithMembershipAsync(context, admin.AccessToken);
    var first = await RegisterAsync(context.Client, "Primera", UniqueEmail("primera"));
    var second = await RegisterAsync(context.Client, "Segunda", UniqueEmail("segunda"));
    var third = await RegisterAsync(context.Client, "Tercera", UniqueEmail("tercera"));

    foreach (var account in new[] { first, second })
    {
      AuthenticateAs(context.Client, account.AccessToken);
      using var submit = await context.Client.PostAsJsonAsync($"{OwnUrl}/claims",
          new CustomerClaimCreateRequest(account.User.FullName, "3001234567", account.User.Email, null, Guid.NewGuid()));
      Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
    }

    AuthenticateAs(context.Client, admin.AccessToken);
    var claims = (await context.Client.GetPageItemsAsync<CustomerClaimView>(AdminUrl));
    Assert.Equal(2, claims.Count);
    var statuses = await Task.WhenAll(claims.Select(async claim =>
    {
      using var response = await context.Client.PostAsJsonAsync($"{AdminUrl}/{claim.Id}/approve",
          new CustomerClaimApproveRequest(claim.Version, historicalId, true, null));
      return response.StatusCode;
    }));
    Assert.Single(statuses, status => status == HttpStatusCode.OK);
    Assert.Single(statuses, status => status == HttpStatusCode.Conflict);

    AuthenticateAs(context.Client, third.AccessToken);
    var created = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
    {
      using var response = await context.Client.PostAsJsonAsync(OwnUrl,
          new OwnCustomerProfileCreateRequest("Tercera", "3109876543", third.User.Email));
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      return (await response.Content.ReadFromJsonAsync<OwnCustomerProfileView>())!.Id;
    }));
    Assert.Single(created.Distinct());

    using var scope = context.Factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    Assert.Equal(1, await db.Customers.CountAsync(customer => customer.UserId == third.User.Id));
    Assert.Equal(1, await db.Customers.CountAsync(customer => customer.Id == historicalId && customer.UserId != null));
    Assert.Equal(1, await db.CustomerClaims.CountAsync(claim => claim.Status == Barbershop.Domain.Customers.CustomerClaimStatus.Pending));
  }

  [Fact]
  public async Task ConcurrentClaims_LeaveOnePending_AndRevokingTheCriticalPermissionAppliesToTheSameToken()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var approver = await RegisterWithCustomRoleAsync(context, "approver", PermissionCodes.CustomerClaimsApprove);
    var customer = await RegisterAsync(context.Client, "Carla", UniqueEmail("carla"));

    AuthenticateAs(context.Client, customer.AccessToken);
    var submissions = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
    {
      using var response = await context.Client.PostAsJsonAsync($"{OwnUrl}/claims",
          new CustomerClaimCreateRequest("Carla", "3001234567", customer.User.Email, null, Guid.NewGuid()));
      return response.StatusCode;
    }));
    Assert.Single(submissions, status => status == HttpStatusCode.OK);
    Assert.All(submissions.Where(status => status != HttpStatusCode.OK), status => Assert.Equal(HttpStatusCode.Conflict, status));

    AuthenticateAs(context.Client, approver.AccessToken);
    var claim = Assert.Single((await context.Client.GetPageItemsAsync<CustomerClaimView>(AdminUrl)));

    using (var scope = context.Factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      db.RolePermissions.Remove(await db.RolePermissions.SingleAsync(grant =>
          grant.Role.Name == "Rol approver" && grant.Permission.Code == PermissionCodes.CustomerClaimsApprove));
      await db.SaveChangesAsync();
    }

    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(context.Client, HttpMethod.Get, AdminUrl));
    using var reject = await context.Client.PostAsJsonAsync($"{AdminUrl}/{claim.Id}/reject", new CustomerClaimRejectRequest(claim.Version, "Sin permiso"));
    Assert.Equal(HttpStatusCode.Forbidden, reject.StatusCode);
  }

  // ── Helpers ────────────────────────────────────────────────────────────────────────────────

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

  private async Task<(Guid CustomerId, Guid MembershipId)> HistoricalCustomerWithMembershipAsync(TestHttpContext context, string adminToken)
  {
    Guid serviceId;
    using (var scope = context.Factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var service = new Service("Corte", 25000, DateTime.UtcNow, 30);
      db.Services.Add(service);
      await db.SaveChangesAsync();
      serviceId = service.Id;
    }

    AuthenticateAs(context.Client, adminToken);
    using var plan = await context.Client.PostAsJsonAsync("/api/v1/admin/membership-plans",
        new MembershipPlanUpsertRequest("Oro", null, 250000, "Pool", 10, 90, Terms, null, [new(serviceId, null)], []));
    var planId = (await plan.Content.ReadFromJsonAsync<MembershipPlanView>())!.Id;
    using var created = await context.Client.PostAsJsonAsync("/api/v1/admin/customers",
        new CustomerCreateRequest("Carla Histórica", "300 123 4567", UniqueEmail("historica")));
    var customerId = (await created.Content.ReadFromJsonAsync<CustomerDetailView>())!.Id;
    using var sale = await context.Client.PostAsJsonAsync($"/api/v1/admin/customers/{customerId}/memberships",
        new MembershipSaleRequest(planId, _today, ActivateNow: true));
    Assert.Equal(HttpStatusCode.Created, sale.StatusCode);
    return (customerId, (await sale.Content.ReadFromJsonAsync<CustomerMembershipDetailView>())!.Id);
  }

  private static async Task<HttpStatusCode> StatusAsync(HttpClient client, HttpMethod method, string url)
  {
    using var response = await client.SendAsync(new HttpRequestMessage(method, url));
    return response.StatusCode;
  }

  private static async Task<AuthPayload> RegisterWithCustomRoleAsync(TestHttpContext context, string label, params string[] permissionCodes)
  {
    var email = UniqueEmail(label);
    await RegisterAsync(context.Client, $"Usuario {label}", email);
    using (var scope = context.Factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var user = await db.Users.Include(candidate => candidate.UserRoles).SingleAsync(candidate => candidate.NormalizedEmail == email.ToUpperInvariant());
      var role = new Role($"Rol {label}");
      foreach (var permission in await db.Permissions.Where(permission => permissionCodes.Contains(permission.Code)).ToListAsync())
      {
        role.AddPermission(permission.Id, DateTime.UtcNow);
      }

      db.Roles.Add(role);
      user.UserRoles.Add(new UserRole(user.Id, role.Id, DateTime.UtcNow));
      await db.SaveChangesAsync();
    }

    return await LoginAsync(context.Client, email);
  }

  private static async Task<AuthPayload> RegisterAdminAsync(TestHttpContext context, string label)
  {
    var email = UniqueEmail($"admin-{label}");
    await RegisterAsync(context.Client, $"Admin {label}", email);
    using (var scope = context.Factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var user = await db.Users.Include(candidate => candidate.UserRoles).SingleAsync(candidate => candidate.NormalizedEmail == email.ToUpperInvariant());
      var adminRole = await db.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Admin.ToUpperInvariant());
      user.UserRoles.Add(new UserRole(user.Id, adminRole.Id, DateTime.UtcNow));
      await db.SaveChangesAsync();
    }

    return await LoginAsync(context.Client, email);
  }

  private static async Task<AuthPayload> RegisterAsync(HttpClient client, string fullName, string email)
  {
    client.DefaultRequestHeaders.Authorization = null;
    using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(fullName, email, DefaultPassword, "3001112233"));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<AuthPayload>())!;
  }

  private static async Task<AuthPayload> LoginAsync(HttpClient client, string email)
  {
    client.DefaultRequestHeaders.Authorization = null;
    using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, DefaultPassword));
    Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    return (await login.Content.ReadFromJsonAsync<AuthPayload>())!;
  }

  private static void AuthenticateAs(HttpClient client, string accessToken)
      => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

  private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

  private sealed record AuthPayload(string AccessToken, AuthUserResponse User);

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
