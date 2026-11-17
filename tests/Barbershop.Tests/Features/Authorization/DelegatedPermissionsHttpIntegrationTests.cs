using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Authorization;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Authorization;

/// <summary>
/// ADR-0018 D4 (SONDA BUG-077): <c>loyalty.points.adjust</c> and <c>memberships.payments.confirm</c> are enough on their
/// own. A custom role with one of them reaches the customer, the wallet and the membership it needs, and the write the
/// permission names, without being Admin; everything else stays Admin-only. Both are critical (ADR-0005), so a
/// revocation denies the next request with the same token.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "DelegatedPermissionsHttpIntegration")]
public sealed class DelegatedPermissionsHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public DelegatedPermissionsHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task ARoleWithLoyaltyPointsAdjust_ReachesTheCustomerAndTheWallet_AndAdjusts_ButNothingAdminOnly()
  {
    using var factory = await CreateFactoryAsync();
    if (factory is null) return;
    using var client = factory.CreateClient();

    var admin = await RegisterAdminAsync(factory, client, "adjust");
    var customerId = await CreateCustomerAsync(client, admin.AccessToken);
    var permissions = await GetPermissionsAsync(client, admin.AccessToken);
    var role = await CreateRoleAsync(client, admin.AccessToken, "Ajustador de puntos", permissions[PermissionCodes.LoyaltyPointsAdjust]);
    var adjuster = await CreateUserWithRolesAsync(client, admin.AccessToken, "adjuster", role.Id);

    AuthenticateAs(client, adjuster.AccessToken);
    Assert.Equal(HttpStatusCode.OK, await StatusAsync(client.GetAsync("/api/v1/admin/customers?q=Carla")));
    Assert.Equal(HttpStatusCode.OK, await StatusAsync(client.GetAsync($"/api/v1/admin/customers/{customerId}")));
    Assert.Equal(HttpStatusCode.OK, await StatusAsync(client.GetAsync($"/api/v1/admin/customers/{customerId}/loyalty")));
    Assert.Equal(HttpStatusCode.OK, await StatusAsync(client.GetAsync($"/api/v1/admin/customers/{customerId}/loyalty/movements")));
    Assert.Equal(HttpStatusCode.OK, await StatusAsync(client.GetAsync($"/api/v1/admin/customers/{customerId}/memberships")));

    // The write the permission names goes past authorization (the wallet rules decide the rest).
    var adjust = await StatusAsync(client.PostAsJsonAsync(
        $"/api/v1/admin/customers/{customerId}/loyalty/adjustments",
        new { Direction = "Credit", Points = 10, Reason = "Bono por prueba", IdempotencyKey = Guid.NewGuid() }));
    Assert.NotEqual(HttpStatusCode.Forbidden, adjust);
    Assert.NotEqual(HttpStatusCode.Unauthorized, adjust);

    // Admin-only actions stay closed.
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.PostAsJsonAsync(
        "/api/v1/admin/customers", new { FullName = "Otra Persona", Phone = "3009998877", Email = "otra@example.com" })));
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.GetAsync("/api/v1/admin/customers/duplicates")));
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.PostAsJsonAsync(
        $"/api/v1/admin/customers/{customerId}/memberships", new { })));
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.PutAsJsonAsync(
        $"/api/v1/admin/memberships/{Guid.NewGuid()}/income-link", new { IncomeEntryId = Guid.NewGuid() })));

    // ADR-0005: removing the permission from the role denies the next request, with the same token.
    AuthenticateAs(client, admin.AccessToken);
    using (var update = await client.PutAsJsonAsync($"/api/v1/admin/roles/{role.Id}", new RoleUpdateRequest("Ajustador de puntos", [])))
    {
      Assert.Equal(HttpStatusCode.OK, update.StatusCode);
    }

    AuthenticateAs(client, adjuster.AccessToken);
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.GetAsync($"/api/v1/admin/customers/{customerId}/loyalty")));
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.PostAsJsonAsync(
        $"/api/v1/admin/customers/{customerId}/loyalty/adjustments",
        new { Direction = "Credit", Points = 10, Reason = "Bono por prueba", IdempotencyKey = Guid.NewGuid() })));
  }

  [Fact]
  public async Task ARoleWithMembershipPaymentsConfirm_ReachesTheMembershipAndLinksItsPayment_ButCannotAdjustPoints()
  {
    using var factory = await CreateFactoryAsync();
    if (factory is null) return;
    using var client = factory.CreateClient();

    var admin = await RegisterAdminAsync(factory, client, "payments");
    var customerId = await CreateCustomerAsync(client, admin.AccessToken);
    var permissions = await GetPermissionsAsync(client, admin.AccessToken);
    var role = await CreateRoleAsync(client, admin.AccessToken, "Caja", permissions[PermissionCodes.MembershipPaymentsConfirm]);
    var cashier = await CreateUserWithRolesAsync(client, admin.AccessToken, "cashier", role.Id);

    AuthenticateAs(client, cashier.AccessToken);
    Assert.Equal(HttpStatusCode.OK, await StatusAsync(client.GetAsync($"/api/v1/admin/customers/{customerId}")));
    Assert.Equal(HttpStatusCode.OK, await StatusAsync(client.GetAsync($"/api/v1/admin/customers/{customerId}/memberships")));
    Assert.Equal(HttpStatusCode.OK, await StatusAsync(client.GetAsync($"/api/v1/admin/customers/{customerId}/loyalty")));

    // Past authorization: an unknown membership is a 404, not a 403.
    Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client.GetAsync($"/api/v1/admin/memberships/{Guid.NewGuid()}")));
    var link = await StatusAsync(client.PutAsJsonAsync(
        $"/api/v1/admin/memberships/{Guid.NewGuid()}/income-link", new { IncomeEntryId = Guid.NewGuid() }));
    Assert.NotEqual(HttpStatusCode.Forbidden, link);
    Assert.NotEqual(HttpStatusCode.Unauthorized, link);

    // Only what the permission names: no points adjustment, no unlinking, no sale.
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.PostAsJsonAsync(
        $"/api/v1/admin/customers/{customerId}/loyalty/adjustments",
        new { Direction = "Credit", Points = 10, Reason = "Bono por prueba", IdempotencyKey = Guid.NewGuid() })));
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.PostAsJsonAsync(
        $"/api/v1/admin/memberships/{Guid.NewGuid()}/income-unlink", new { })));

    // Without the role (so without either permission) none of it is reachable, with the same token.
    // (One user per test: the sign-in limit per IP, SONDA BUG-117, allows five logins a minute.)
    AuthenticateAs(client, admin.AccessToken);
    using (var unassign = await client.PatchAsJsonAsync($"/api/v1/admin/users/{cashier.User.Id}/roles", new { RoleIds = Array.Empty<Guid>() }))
    {
      Assert.Equal(HttpStatusCode.OK, unassign.StatusCode);
    }

    AuthenticateAs(client, cashier.AccessToken);
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.GetAsync($"/api/v1/admin/customers/{customerId}")));
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.GetAsync($"/api/v1/admin/customers/{customerId}/loyalty")));
  }

  private async Task<IntegrationTestFactory?> CreateFactoryAsync()
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return null;
    }

    var factory = new IntegrationTestFactory(_postgres.ConnectionString);
    await factory.ResetDatabaseAsync();
    return factory;
  }

  private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> request)
  {
    using var response = await request;
    return response.StatusCode;
  }

  private static async Task<Guid> CreateCustomerAsync(HttpClient client, string adminToken)
  {
    AuthenticateAs(client, adminToken);
    using var response = await client.PostAsJsonAsync(
        "/api/v1/admin/customers", new { FullName = "Carla Cliente", Phone = "3001234567", Email = $"carla-{Guid.NewGuid():N}@example.com" });
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    var created = await response.Content.ReadFromJsonAsync<IdPayload>();
    return created!.Id;
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
  private static async Task<AuthPayload> CreateUserWithRolesAsync(HttpClient client, string adminToken, string label, params Guid[] roleIds)
  {
    var email = UniqueEmail(label);
    var registered = await RegisterAsync(client, $"User {label}", email);
    if (roleIds.Length > 0)
    {
      AuthenticateAs(client, adminToken);
      using var assign = await client.PatchAsJsonAsync($"/api/v1/admin/users/{registered.User.Id}/roles", new { RoleIds = roleIds });
      Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
    }

    return await LoginAsync(client, email);
  }

  private static async Task<AuthPayload> RegisterAsync(HttpClient client, string fullName, string email)
  {
    client.DefaultRequestHeaders.Authorization = null;
    using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(fullName, email, DefaultPassword, "3001112233"));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    var payload = await response.Content.ReadFromJsonAsync<AuthPayload>();
    Assert.NotNull(payload);
    return payload!;
  }

  private static async Task<AuthPayload> RegisterAdminAsync(IntegrationTestFactory factory, HttpClient client, string label)
  {
    var email = UniqueEmail($"admin-{label}");
    await RegisterAsync(client, $"Admin {label}", email);

    using (var scope = factory.Services.CreateScope())
    {
      var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var user = await dbContext.Users.Include(candidate => candidate.UserRoles)
          .SingleAsync(candidate => candidate.NormalizedEmail == email.ToUpperInvariant());
      var adminRole = await dbContext.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Admin.ToUpperInvariant());
      user.UserRoles.Add(new UserRole(user.Id, adminRole.Id, DateTime.UtcNow));
      await dbContext.SaveChangesAsync();
    }

    return await LoginAsync(client, email);
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

  private static void AuthenticateAs(HttpClient client, string accessToken)
      => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

  private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

  private sealed record AuthPayload(string AccessToken, AuthUserResponse User);

  private sealed record IdPayload(Guid Id);
}
