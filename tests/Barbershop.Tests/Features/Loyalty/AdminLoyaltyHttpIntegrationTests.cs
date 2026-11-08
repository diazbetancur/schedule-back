using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Authorization;
using Barbershop.Application.Loyalty.Admin;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Application.Services.Admin;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using Barbershop.Domain.Services;

namespace Barbershop.Tests.Features.Loyalty;

/// <summary>
/// Global loyalty settings and the fixed points of services and membership types over real HTTP, JWT and
/// PostgreSQL: permissions (loyalty.settings.manage is critical, so it is checked against the database),
/// optimistic concurrency on xmin and the audit trail.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "AdminLoyaltyHttpIntegration")]
public sealed class AdminLoyaltyHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";
  private const string SettingsUrl = "/api/v1/admin/loyalty/settings";
  private const string Terms = "La membresía es personal e intransferible.";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public AdminLoyaltyHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task Admin_ReadsTheDefaults_UpdatesTheGlobalValidity_AndTheChangeIsAudited()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context, "settings");
    AuthenticateAs(context.Client, admin.AccessToken);

    var initial = await context.Client.GetFromJsonAsync<LoyaltySettingsView>(SettingsUrl);
    Assert.NotNull(initial);
    Assert.False(initial!.IsEnabled);
    Assert.Equal(LoyaltySettings.DefaultPointsValidityDays, initial.PointsValidityDays);
    Assert.NotEqual(0u, initial.Version);

    using var update = await context.Client.PutAsJsonAsync(SettingsUrl, new LoyaltySettingsUpdateRequest(true, 180, initial.Version));
    Assert.Equal(HttpStatusCode.OK, update.StatusCode);
    var updated = await update.Content.ReadFromJsonAsync<LoyaltySettingsView>();
    Assert.True(updated!.IsEnabled);
    Assert.Equal(180, updated.PointsValidityDays);
    Assert.NotEqual(initial.Version, updated.Version);
    Assert.Equal("Admin settings", updated.UpdatedByName);

    // PostgreSQL stores microseconds, so the re-read timestamp may drop the last tick digit.
    var reread = (await context.Client.GetFromJsonAsync<LoyaltySettingsView>(SettingsUrl))!;
    Assert.Equal(updated with { UpdatedAt = null }, reread with { UpdatedAt = null });
    Assert.Equal(updated.UpdatedAt!.Value, reread.UpdatedAt!.Value, TimeSpan.FromMilliseconds(1));

    var changes = await context.Client.GetPageItemsAsync<LoyaltySettingsChangeView>($"{SettingsUrl}/changes");
    var change = Assert.Single(changes!);
    Assert.Equal((false, true, LoyaltySettings.DefaultPointsValidityDays, 180),
        (change.PreviousIsEnabled, change.NewIsEnabled, change.PreviousPointsValidityDays, change.NewPointsValidityDays));
    Assert.Equal(admin.User.Id, change.ChangedByUserId);
  }

  [Fact]
  public async Task InvalidSettings_AreRejectedWithFieldErrors_OrAsABadRequest()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context, "invalid");
    AuthenticateAs(context.Client, admin.AccessToken);
    var current = (await context.Client.GetFromJsonAsync<LoyaltySettingsView>(SettingsUrl))!;

    foreach (var days in new[] { 0, -1, LoyaltySettings.MaxPointsValidityDays + 1 })
    {
      using var response = await context.Client.PutAsJsonAsync(SettingsUrl, new LoyaltySettingsUpdateRequest(true, days, current.Version));
      Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
      Assert.Contains("pointsValidityDays", await response.Content.ReadAsStringAsync());
    }

    using (var missingVersion = await context.Client.PutAsJsonAsync(SettingsUrl, new { isEnabled = true, pointsValidityDays = 30 }))
    {
      Assert.Equal(HttpStatusCode.UnprocessableEntity, missingVersion.StatusCode);
      Assert.Contains("version", await missingVersion.Content.ReadAsStringAsync());
    }

    // A fractional number of days cannot even be read as the request.
    using (var fractional = await context.Client.PutAsJsonAsync(
        SettingsUrl, new { isEnabled = true, pointsValidityDays = 30.5, version = current.Version }))
    {
      Assert.Equal(HttpStatusCode.BadRequest, fractional.StatusCode);
    }

    Assert.Equal(current, await context.Client.GetFromJsonAsync<LoyaltySettingsView>(SettingsUrl));
    Assert.Empty((await context.Client.GetPageItemsAsync<LoyaltySettingsChangeView>($"{SettingsUrl}/changes")));
  }

  [Fact]
  public async Task AStaleVersion_IsAConflict_AndConcurrentUpdatesLetExactlyOneWin()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context, "concurrency");
    AuthenticateAs(context.Client, admin.AccessToken);
    var original = (await context.Client.GetFromJsonAsync<LoyaltySettingsView>(SettingsUrl))!;

    using (var first = await context.Client.PutAsJsonAsync(SettingsUrl, new LoyaltySettingsUpdateRequest(true, 30, original.Version)))
    {
      Assert.Equal(HttpStatusCode.OK, first.StatusCode);
    }

    // A second editor still holding the original version is told to reload instead of overwriting.
    using (var stale = await context.Client.PutAsJsonAsync(SettingsUrl, new LoyaltySettingsUpdateRequest(false, 60, original.Version)))
    {
      Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
      Assert.Contains("Recarga", await stale.Content.ReadAsStringAsync());
    }

    var current = (await context.Client.GetFromJsonAsync<LoyaltySettingsView>(SettingsUrl))!;
    Assert.Equal((true, 30), (current.IsEnabled, current.PointsValidityDays));

    // Simultaneous requests with the same version: the database lets only one of them update the row.
    var attempts = Enumerable.Range(1, 6)
        .Select(async index =>
        {
          using var response = await context.Client.PutAsJsonAsync(
              SettingsUrl, new LoyaltySettingsUpdateRequest(true, 100 + index, current.Version));
          return response.StatusCode;
        })
        .ToArray();
    var statuses = await Task.WhenAll(attempts);

    Assert.Single(statuses, status => status == HttpStatusCode.OK);
    Assert.All(statuses.Where(status => status != HttpStatusCode.OK), status => Assert.Equal(HttpStatusCode.Conflict, status));

    var changes = (await context.Client.GetPageItemsAsync<LoyaltySettingsChangeView>($"{SettingsUrl}/changes"));
    Assert.Equal(2, changes.Count);
    var final = (await context.Client.GetFromJsonAsync<LoyaltySettingsView>(SettingsUrl))!;
    Assert.Equal(changes[0].NewPointsValidityDays, final.PointsValidityDays);
  }

  [Fact]
  public async Task OnlyUsersWithTheCurrentPermission_ReachTheSettings_AndARevocationAppliesToTheSameToken()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context, "permissions");
    var permissions = await GetPermissionsAsync(context.Client, admin.AccessToken);
    var managerRole = await CreateRoleAsync(context.Client, admin.AccessToken, "Fidelización", permissions[PermissionCodes.LoyaltySettingsManage]);
    var manager = await CreateUserWithRolesAsync(context, admin.AccessToken, "manager", managerRole.Id);
    var customer = await RegisterAsync(context.Client, "Plain Customer", UniqueEmail("customer"));

    context.Client.DefaultRequestHeaders.Authorization = null;
    using (var anonymous = await context.Client.GetAsync(SettingsUrl))
    {
      Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(context.Client, customer.AccessToken, SettingsUrl));

    // A non-admin role with the permission manages the settings. (The auth endpoints allow five credential
    // requests per minute, so this test signs in only the admin and the manager.)
    Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(context.Client, manager.AccessToken, SettingsUrl));
    AuthenticateAs(context.Client, manager.AccessToken);
    var current = (await context.Client.GetFromJsonAsync<LoyaltySettingsView>(SettingsUrl))!;
    using (var update = await context.Client.PutAsJsonAsync(SettingsUrl, new LoyaltySettingsUpdateRequest(true, 45, current.Version)))
    {
      Assert.Equal(HttpStatusCode.OK, update.StatusCode);
      current = (await update.Content.ReadFromJsonAsync<LoyaltySettingsView>())!;
    }

    AuthenticateAs(context.Client, admin.AccessToken);
    using (var revoke = await context.Client.PutAsJsonAsync(
        $"/api/v1/admin/roles/{managerRole.Id}", new RoleUpdateRequest("Fidelización", [permissions[PermissionCodes.SalesRegister]])))
    {
      Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
    }

    // Same token, still carrying the claim: reads and writes are denied on the next request.
    Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(context.Client, manager.AccessToken, SettingsUrl));
    AuthenticateAs(context.Client, manager.AccessToken);
    using (var denied = await context.Client.PutAsJsonAsync(SettingsUrl, new LoyaltySettingsUpdateRequest(false, 10, current.Version)))
    {
      Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(context.Client, admin.AccessToken, SettingsUrl));
    AuthenticateAs(context.Client, admin.AccessToken);
    var final = (await context.Client.GetFromJsonAsync<LoyaltySettingsView>(SettingsUrl))!;
    Assert.Equal((true, 45), (final.IsEnabled, final.PointsValidityDays));
  }

  [Fact]
  public async Task ServicePoints_DefaultToZero_AreKeptByOldClients_AndValidated()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context, "service-points");
    AuthenticateAs(context.Client, admin.AccessToken);

    // A client that does not know about loyalty (the request without loyaltyPoints).
    var legacy = await CreateServiceAsync(context.Client, new { name = "Barba", basePrice = 15000, businessPercentage = 30, generalServiceId = ServiceCatalogDefaults.CorteGeneralServiceId });
    Assert.Equal(0, legacy.LoyaltyPoints);

    var withPoints = await CreateServiceAsync(context.Client, new ServiceCreateRequest("Corte", 25000, 30, 10, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));
    Assert.Equal(10, withPoints.LoyaltyPoints);

    using (var keep = await context.Client.PutAsJsonAsync(
        $"/api/v1/admin/services/{withPoints.Id}", new { name = "Corte clásico", basePrice = 26000, businessPercentage = 30 }))
    {
      Assert.Equal(HttpStatusCode.OK, keep.StatusCode);
      Assert.Equal(10, (await keep.Content.ReadFromJsonAsync<ServiceView>())!.LoyaltyPoints);
    }

    using (var zero = await context.Client.PutAsJsonAsync(
        $"/api/v1/admin/services/{withPoints.Id}", new ServiceUpdateRequest("Corte clásico", 26000, 30, 0)))
    {
      Assert.Equal(HttpStatusCode.OK, zero.StatusCode);
      Assert.Equal(0, (await zero.Content.ReadFromJsonAsync<ServiceView>())!.LoyaltyPoints);
    }

    foreach (var points in new[] { -1, LoyaltySettings.MaxPointsPerItem + 1 })
    {
      using var invalid = await context.Client.PutAsJsonAsync(
          $"/api/v1/admin/services/{withPoints.Id}", new ServiceUpdateRequest("Corte clásico", 26000, 30, points));
      Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
      Assert.Contains("loyaltyPoints", await invalid.Content.ReadAsStringAsync());
    }

    using (var fractional = await context.Client.PutAsJsonAsync(
        $"/api/v1/admin/services/{withPoints.Id}", new { name = "Corte clásico", basePrice = 26000, businessPercentage = 30, loyaltyPoints = 2.5 }))
    {
      Assert.Equal(HttpStatusCode.BadRequest, fractional.StatusCode);
    }

    var listed = await context.Client.GetPageItemsAsync<ServiceView>("/api/v1/admin/services");
    Assert.Equal([("Barba", 0), ("Corte clásico", 0)], listed!.Select(service => (service.Name, service.LoyaltyPoints)));

    var audit = await ReadPointsChangesAsync(context);
    Assert.Equal([(0, 10), (10, 0)], audit.Select(change => (change.PreviousPoints, change.NewPoints)));
    Assert.All(audit, change => Assert.Equal((LoyaltyPointsTarget.Service, withPoints.Id, admin.User.Id), (change.TargetType, change.TargetId, change.ChangedByUserId)));
  }

  [Fact]
  public async Task MembershipPlanPoints_DefaultToZero_AreKeptWhenOmitted_AndValidated()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context, "plan-points");
    AuthenticateAs(context.Client, admin.AccessToken);
    var haircut = await CreateServiceAsync(context.Client, new ServiceCreateRequest("Corte", 25000, 30, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));

    var legacy = await CreatePlanAsync(context.Client, PlanRequest("Plata", haircut.Id, loyaltyPoints: null));
    Assert.Equal(0, legacy.LoyaltyPoints);

    var gold = await CreatePlanAsync(context.Client, PlanRequest("Oro", haircut.Id, loyaltyPoints: 200));
    Assert.Equal(200, gold.LoyaltyPoints);

    using (var keep = await context.Client.PutAsJsonAsync($"/api/v1/admin/membership-plans/{gold.Id}", PlanRequest("Oro", haircut.Id, null)))
    {
      Assert.Equal(HttpStatusCode.OK, keep.StatusCode);
      Assert.Equal(200, (await keep.Content.ReadFromJsonAsync<MembershipPlanView>())!.LoyaltyPoints);
    }

    using (var invalid = await context.Client.PutAsJsonAsync($"/api/v1/admin/membership-plans/{gold.Id}", PlanRequest("Oro", haircut.Id, -3)))
    {
      Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
      Assert.Contains("loyaltyPoints", await invalid.Content.ReadAsStringAsync());
    }

    using (var zero = await context.Client.PutAsJsonAsync($"/api/v1/admin/membership-plans/{gold.Id}", PlanRequest("Oro", haircut.Id, 0)))
    {
      Assert.Equal(HttpStatusCode.OK, zero.StatusCode);
      Assert.Equal(0, (await zero.Content.ReadFromJsonAsync<MembershipPlanView>())!.LoyaltyPoints);
    }

    var audit = await ReadPointsChangesAsync(context);
    Assert.Equal([(0, 200), (200, 0)], audit.Select(change => (change.PreviousPoints, change.NewPoints)));
    Assert.All(audit, change => Assert.Equal((LoyaltyPointsTarget.MembershipPlan, gold.Id), (change.TargetType, change.TargetId)));
  }

  [Fact]
  public async Task CatalogPoints_NeedTheCurrentPermission_EvenForAnAdmin_WhileTheRestOfTheFormKeepsWorking()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context, "points-revoked");
    AuthenticateAs(context.Client, admin.AccessToken);
    var haircut = await CreateServiceAsync(context.Client, new ServiceCreateRequest("Corte", 25000, 30, 5, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));
    var plan = await CreatePlanAsync(context.Client, PlanRequest("Oro", haircut.Id, 50));

    await RemovePermissionFromAdminRoleAsync(context, PermissionCodes.LoyaltySettingsManage);

    // Same admin token, which still carries the claim.
    using (var service = await context.Client.PutAsJsonAsync(
        $"/api/v1/admin/services/{haircut.Id}", new ServiceUpdateRequest("Corte", 25000, 30, 8)))
    {
      Assert.Equal(HttpStatusCode.Forbidden, service.StatusCode);
      Assert.Contains("puntos de fidelización", await service.Content.ReadAsStringAsync());
    }

    using (var newService = await context.Client.PostAsJsonAsync("/api/v1/admin/services", new ServiceCreateRequest("Barba", 15000, 30, 0, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId)))
    {
      Assert.Equal(HttpStatusCode.Forbidden, newService.StatusCode);
    }

    using (var planUpdate = await context.Client.PutAsJsonAsync($"/api/v1/admin/membership-plans/{plan.Id}", PlanRequest("Oro", haircut.Id, 70)))
    {
      Assert.Equal(HttpStatusCode.Forbidden, planUpdate.StatusCode);
    }

    Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(context.Client, admin.AccessToken, SettingsUrl));

    // Without points the catalogs behave exactly as before, and the stored points stay untouched.
    using (var service = await context.Client.PutAsJsonAsync(
        $"/api/v1/admin/services/{haircut.Id}", new ServiceUpdateRequest("Corte clásico", 26000, 30)))
    {
      Assert.Equal(HttpStatusCode.OK, service.StatusCode);
      Assert.Equal(5, (await service.Content.ReadFromJsonAsync<ServiceView>())!.LoyaltyPoints);
    }

    using (var planUpdate = await context.Client.PutAsJsonAsync($"/api/v1/admin/membership-plans/{plan.Id}", PlanRequest("Oro", haircut.Id, null)))
    {
      Assert.Equal(HttpStatusCode.OK, planUpdate.StatusCode);
      Assert.Equal(50, (await planUpdate.Content.ReadFromJsonAsync<MembershipPlanView>())!.LoyaltyPoints);
    }

    Assert.Equal(2, (await ReadPointsChangesAsync(context)).Count);
  }

  [Fact]
  public async Task Customers_CannotSetCatalogPoints()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var customer = await RegisterAsync(context.Client, "Plain Customer", UniqueEmail("customer"));
    AuthenticateAs(context.Client, customer.AccessToken);

    using var response = await context.Client.PostAsJsonAsync("/api/v1/admin/services", new ServiceCreateRequest("Corte", 25000, 30, 10, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));
    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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

  private static MembershipPlanUpsertRequest PlanRequest(string name, Guid serviceId, int? loyaltyPoints) =>
      new(name, null, 250000, "Pool", 10, 90, Terms, null, [new(serviceId, null)], [], loyaltyPoints);

  private static async Task<ServiceView> CreateServiceAsync(HttpClient client, object request)
  {
    using var response = await client.PostAsJsonAsync("/api/v1/admin/services", request);
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<ServiceView>())!;
  }

  private static async Task<MembershipPlanView> CreatePlanAsync(HttpClient client, MembershipPlanUpsertRequest request)
  {
    using var response = await client.PostAsJsonAsync("/api/v1/admin/membership-plans", request);
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<MembershipPlanView>())!;
  }

  private static async Task<List<LoyaltyPointsChange>> ReadPointsChangesAsync(TestHttpContext context)
  {
    using var scope = context.Factory.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    return await dbContext.LoyaltyPointsChanges.AsNoTracking().OrderBy(change => change.ChangedAt).ToListAsync();
  }

  private static async Task RemovePermissionFromAdminRoleAsync(TestHttpContext context, string permissionCode)
  {
    using var scope = context.Factory.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var adminRole = await dbContext.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Admin.ToUpperInvariant());
    var permission = await dbContext.Permissions.SingleAsync(candidate => candidate.Code == permissionCode);
    dbContext.RolePermissions.Remove(await dbContext.RolePermissions.SingleAsync(
        grant => grant.RoleId == adminRole.Id && grant.PermissionId == permission.Id));
    await dbContext.SaveChangesAsync();
  }

  private static async Task<HttpStatusCode> GetStatusAsync(HttpClient client, string accessToken, string url)
  {
    AuthenticateAs(client, accessToken);
    using var response = await client.GetAsync(url);
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
    return (await response.Content.ReadFromJsonAsync<RoleView>())!;
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
    return (await response.Content.ReadFromJsonAsync<AuthPayload>())!;
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
    return (await login.Content.ReadFromJsonAsync<AuthPayload>())!;
  }

  private static void AuthenticateAs(HttpClient client, string accessToken)
  {
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
  }

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
