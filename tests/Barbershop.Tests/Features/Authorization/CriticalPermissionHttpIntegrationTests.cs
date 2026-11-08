using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Authorization;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Authorization;

/// <summary>
/// The permission policies over real HTTP, JWT and PostgreSQL. No production endpoint uses the critical policies yet,
/// so the tests mount one probe endpoint per permission (test host only), protected exactly like a future endpoint:
/// <c>RequireAuthorization(PermissionPolicyNames.For(code))</c>.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "CriticalPermissionHttpIntegration")]
public sealed class CriticalPermissionHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";
  private const string ProbeBasePath = "/__tests/permission-probe";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public CriticalPermissionHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task RemovingTheCriticalPermissionFromTheRole_DeniesTheNextRequest_WithTheSameToken()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context, "revoke-permission");
    var permissions = await GetPermissionsAsync(context.Client, admin.AccessToken);
    var role = await CreateRoleAsync(
        context.Client, admin.AccessToken, "Aprobador", permissions[PermissionCodes.LoyaltyRedemptionsApprove], permissions[PermissionCodes.SalesRegister]);
    var approver = await CreateUserWithRolesAsync(context, admin.AccessToken, "approver", role.Id);

    Assert.Equal(HttpStatusCode.OK, await ProbeAsync(context.Client, approver.AccessToken, PermissionCodes.LoyaltyRedemptionsApprove));
    // Approving and delivering stay separate: the role does not grant delivery.
    Assert.Equal(HttpStatusCode.Forbidden, await ProbeAsync(context.Client, approver.AccessToken, PermissionCodes.LoyaltyRedemptionsDeliver));

    AuthenticateAs(context.Client, admin.AccessToken);
    using (var update = await context.Client.PutAsJsonAsync(
        $"/api/v1/admin/roles/{role.Id}", new RoleUpdateRequest("Aprobador", [permissions[PermissionCodes.SalesRegister]])))
    {
      Assert.Equal(HttpStatusCode.OK, update.StatusCode);
    }

    // The token issued before the revocation still carries the claim, and is still rejected.
    Assert.Contains(ReadPermissionClaims(approver.AccessToken), code => code == PermissionCodes.LoyaltyRedemptionsApprove);
    Assert.Equal(HttpStatusCode.Forbidden, await ProbeAsync(context.Client, approver.AccessToken, PermissionCodes.LoyaltyRedemptionsApprove));
    // The permission the role kept, a non-critical one, keeps working as before.
    Assert.Equal(HttpStatusCode.OK, await ProbeAsync(context.Client, approver.AccessToken, PermissionCodes.SalesRegister));
  }

  [Fact]
  public async Task RemovingTheRoleFromTheUser_DeniesTheNextCriticalRequest_WithTheSameToken()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context, "revoke-role");
    var permissions = await GetPermissionsAsync(context.Client, admin.AccessToken);
    var role = await CreateRoleAsync(context.Client, admin.AccessToken, "Ajustador", permissions[PermissionCodes.LoyaltyPointsAdjust]);
    var adjuster = await CreateUserWithRolesAsync(context, admin.AccessToken, "adjuster", role.Id);
    Assert.Equal(HttpStatusCode.OK, await ProbeAsync(context.Client, adjuster.AccessToken, PermissionCodes.LoyaltyPointsAdjust));

    AuthenticateAs(context.Client, admin.AccessToken);
    using (var unassign = await context.Client.PatchAsJsonAsync(
        $"/api/v1/admin/users/{adjuster.User.Id}/roles", new { RoleIds = Array.Empty<Guid>() }))
    {
      Assert.Equal(HttpStatusCode.OK, unassign.StatusCode);
    }

    Assert.Equal(HttpStatusCode.Forbidden, await ProbeAsync(context.Client, adjuster.AccessToken, PermissionCodes.LoyaltyPointsAdjust));
  }

  [Fact]
  public async Task DeactivatingTheAccount_DeniesTheNextCriticalRequest_WithTheSameToken()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context, "deactivate");
    var permissions = await GetPermissionsAsync(context.Client, admin.AccessToken);
    var role = await CreateRoleAsync(context.Client, admin.AccessToken, "Entregador", permissions[PermissionCodes.LoyaltyRedemptionsDeliver]);
    var deliverer = await CreateUserWithRolesAsync(context, admin.AccessToken, "deliverer", role.Id);
    Assert.Equal(HttpStatusCode.OK, await ProbeAsync(context.Client, deliverer.AccessToken, PermissionCodes.LoyaltyRedemptionsDeliver));

    AuthenticateAs(context.Client, admin.AccessToken);
    using (var deactivate = await context.Client.DeleteAsync($"/api/v1/admin/users/{deliverer.User.Id}"))
    {
      Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);
    }

    Assert.Equal(HttpStatusCode.Forbidden, await ProbeAsync(context.Client, deliverer.AccessToken, PermissionCodes.LoyaltyRedemptionsDeliver));
  }

  [Fact]
  public async Task SeededRoles_PassExactlyTheirPolicies_AndAnonymousCallsAreUnauthorized()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context, "seeded");
    var customer = await RegisterAsync(context.Client, "Plain Customer", UniqueEmail("customer"));

    foreach (var code in PermissionCodes.All)
    {
      Assert.Equal(HttpStatusCode.OK, await ProbeAsync(context.Client, admin.AccessToken, code));
      Assert.Equal(HttpStatusCode.Forbidden, await ProbeAsync(context.Client, customer.AccessToken, code));
    }

    context.Client.DefaultRequestHeaders.Authorization = null;
    using var anonymous = await context.Client.GetAsync($"{ProbeBasePath}/{PermissionCodes.LoyaltyRedemptionsApprove}");
    Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
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

    var probeFactory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(
        services => services.AddTransient<IStartupFilter, PermissionProbeStartupFilter>()));
    return new TestHttpContext(factory, probeFactory, probeFactory.CreateClient());
  }

  private static async Task<HttpStatusCode> ProbeAsync(HttpClient client, string accessToken, string permissionCode)
  {
    AuthenticateAs(client, accessToken);
    using var response = await client.GetAsync($"{ProbeBasePath}/{permissionCode}");
    return response.StatusCode;
  }

  private static async Task<IReadOnlyDictionary<string, Guid>> GetPermissionsAsync(HttpClient client, string adminToken)
  {
    AuthenticateAs(client, adminToken);
    var permissions = await client.GetFromJsonAsync<List<PermissionView>>("/api/v1/admin/permissions");
    Assert.NotNull(permissions);
    return permissions!.ToDictionary(permission => permission.Code, permission => permission.Id, StringComparer.Ordinal);
  }

  private static async Task<RoleView> CreateRoleAsync(HttpClient client, string adminToken, string name, params Guid[] permissionIds)
  {
    AuthenticateAs(client, adminToken);
    using var response = await client.PostAsJsonAsync("/api/v1/admin/roles", new RoleCreateRequest(name, permissionIds));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);

    var role = await response.Content.ReadFromJsonAsync<RoleView>();
    Assert.NotNull(role);
    return role!;
  }

  /// <summary>Registers a customer, gives them the roles and signs them in again so the token carries the new claims.</summary>
  private static async Task<AuthPayload> CreateUserWithRolesAsync(TestHttpContext context, string adminToken, string label, params Guid[] roleIds)
  {
    var email = UniqueEmail(label);
    var registered = await RegisterAsync(context.Client, $"User {label}", email);

    AuthenticateAs(context.Client, adminToken);
    using (var assign = await context.Client.PatchAsJsonAsync($"/api/v1/admin/users/{registered.User.Id}/roles", new { RoleIds = roleIds }))
    {
      Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
    }

    return await LoginAsync(context.Client, email);
  }

  private static async Task<AuthPayload> RegisterAsync(HttpClient client, string fullName, string email)
  {
    client.DefaultRequestHeaders.Authorization = null;
    using var response = await client.PostAsJsonAsync(
        "/api/v1/auth/register", new RegisterRequest(fullName, email, DefaultPassword, "3001112233"));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);

    var payload = await response.Content.ReadFromJsonAsync<AuthPayload>();
    Assert.NotNull(payload);
    return payload!;
  }

  private static async Task<AuthPayload> RegisterAdminAsync(TestHttpContext context, string label)
  {
    var email = UniqueEmail($"admin-{label}");
    await RegisterAsync(context.Client, $"Admin {label}", email);

    using (var scope = context.Factory.Services.CreateScope())
    {
      var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var user = await dbContext.Users.Include(candidate => candidate.UserRoles)
          .SingleAsync(candidate => candidate.NormalizedEmail == email.ToUpperInvariant());
      var adminRole = await dbContext.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Admin.ToUpperInvariant());
      user.UserRoles.Add(new UserRole(user.Id, adminRole.Id, DateTime.UtcNow));
      await dbContext.SaveChangesAsync();
    }

    return await LoginAsync(context.Client, email);
  }

  private static async Task<AuthPayload> LoginAsync(HttpClient client, string email)
  {
    client.DefaultRequestHeaders.Authorization = null;
    using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, DefaultPassword));
    Assert.Equal(HttpStatusCode.OK, login.StatusCode);

    var payload = await login.Content.ReadFromJsonAsync<AuthPayload>();
    Assert.NotNull(payload);
    return payload!;
  }

  private static IEnumerable<string> ReadPermissionClaims(string accessToken)
      => new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims
          .Where(claim => claim.Type == PermissionClaimTypes.Permission)
          .Select(claim => claim.Value);

  private static void AuthenticateAs(HttpClient client, string accessToken)
  {
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
  }

  private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

  private sealed record AuthPayload(string AccessToken, AuthUserResponse User);

  /// <summary>Mounts GET {ProbeBasePath}/{code} for every permission, each behind that permission's policy.</summary>
  private sealed class PermissionProbeStartupFilter : IStartupFilter
  {
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
      app.Map(ProbeBasePath, probe =>
      {
        probe.UseRouting();
        probe.UseAuthentication();
        probe.UseAuthorization();
        probe.UseEndpoints(endpoints =>
        {
          foreach (var code in PermissionCodes.All)
          {
            endpoints.MapGet($"/{code}", () => Results.Ok()).RequireAuthorization(PermissionPolicyNames.For(code));
          }
        });
      });

      next(app);
    };
  }

  private sealed class TestHttpContext : IDisposable
  {
    private readonly WebApplicationFactory<Program> _probeFactory;

    public TestHttpContext(IntegrationTestFactory factory, WebApplicationFactory<Program> probeFactory, HttpClient client)
    {
      Factory = factory;
      _probeFactory = probeFactory;
      Client = client;
    }

    public IntegrationTestFactory Factory { get; }
    public HttpClient Client { get; }

    public void Dispose()
    {
      Client.Dispose();
      _probeFactory.Dispose();
      Factory.Dispose();
    }
  }
}
