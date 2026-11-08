using System.IdentityModel.Tokens.Jwt;
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
/// A permission removed from a role stays removed: signing in, refreshing the session and restarting the API (whose
/// startup bootstrap runs the identity seed) never grant it back. System roles cannot be edited through the API, so
/// their revocation is done directly on the (disposable) database.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "PermissionRevocationPersistence")]
public sealed class PermissionRevocationPersistenceHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";
  private const string LoyaltySettingsUrl = "/api/v1/admin/loyalty/settings";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public PermissionRevocationPersistenceHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task ARevokedAdminPermission_StaysRevokedAfterLogin_Refresh_AndRestart()
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return;
    }

    var email = $"admin-{Guid.NewGuid():N}@example.com";
    using (var factory = new IntegrationTestFactory(_postgres.ConnectionString))
    {
      await factory.ResetDatabaseAsync();
      using var client = factory.CreateClient();

      var admin = await RegisterAdminAsync(factory, client, email);
      Assert.Contains(PermissionCodes.LoyaltySettingsManage, admin.User.Permissions);

      await RevokeAsync(factory, RoleNames.Admin, PermissionCodes.LoyaltySettingsManage);
      await RevokeAsync(factory, RoleNames.Staff, PermissionCodes.ServiceRecordsRegister);

      // Login: no longer restores it.
      var afterLogin = await LoginAsync(client, email);
      AssertLacksLoyaltyPermission(afterLogin);
      Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(client, afterLogin.AccessToken, LoyaltySettingsUrl));
      // The rest of the Admin role is untouched.
      Assert.Equal(PermissionCodes.All.Count - 1, afterLogin.User.Permissions.Count);
      Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(client, afterLogin.AccessToken, "/api/v1/admin/services"));

      // Refresh: same.
      client.DefaultRequestHeaders.Authorization = null;
      using var refresh = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { afterLogin.RefreshToken });
      Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
      var refreshed = (await refresh.Content.ReadFromJsonAsync<AuthPayload>())!;
      AssertLacksLoyaltyPermission(refreshed);
      Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(client, refreshed.AccessToken, LoyaltySettingsUrl));
    }

    // Restart: a new API instance over the same database runs the startup seed again.
    using (var restarted = new IntegrationTestFactory(_postgres.ConnectionString))
    {
      using var client = restarted.CreateClient();
      var afterRestart = await LoginAsync(client, email);
      AssertLacksLoyaltyPermission(afterRestart);
      Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(client, afterRestart.AccessToken, LoyaltySettingsUrl));

      using var scope = restarted.Services.CreateScope();
      var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      Assert.Empty(await GrantedCodesAsync(dbContext, RoleNames.Staff));
      Assert.DoesNotContain(PermissionCodes.LoyaltySettingsManage, await GrantedCodesAsync(dbContext, RoleNames.Admin));
      // The catalog itself is complete: nothing was re-created or duplicated.
      Assert.Equal(PermissionCodes.All.Count, await dbContext.Permissions.CountAsync());
    }
  }

  [Fact]
  public async Task APermissionRemovedFromACustomRole_StaysRemovedAfterRestart()
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return;
    }

    Guid roleId;
    using (var factory = new IntegrationTestFactory(_postgres.ConnectionString))
    {
      await factory.ResetDatabaseAsync();
      using var client = factory.CreateClient();
      var admin = await RegisterAdminAsync(factory, client, $"admin-{Guid.NewGuid():N}@example.com");
      AuthenticateAs(client, admin.AccessToken);

      var permissions = (await client.GetFromJsonAsync<List<PermissionView>>("/api/v1/admin/permissions"))!
          .ToDictionary(permission => permission.Code, permission => permission.Id, StringComparer.Ordinal);
      using var create = await client.PostAsJsonAsync("/api/v1/admin/roles", new RoleCreateRequest(
          "Fidelización", [permissions[PermissionCodes.LoyaltySettingsManage], permissions[PermissionCodes.SalesRegister]]));
      roleId = (await create.Content.ReadFromJsonAsync<RoleView>())!.Id;
      using var update = await client.PutAsJsonAsync(
          $"/api/v1/admin/roles/{roleId}", new RoleUpdateRequest("Fidelización", [permissions[PermissionCodes.SalesRegister]]));
      Assert.Equal(HttpStatusCode.OK, update.StatusCode);
    }

    using (var restarted = new IntegrationTestFactory(_postgres.ConnectionString))
    {
      using var client = restarted.CreateClient();
      using var scope = restarted.Services.CreateScope();
      var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var codes = await dbContext.RolePermissions.Where(grant => grant.RoleId == roleId).Select(grant => grant.Permission.Code).ToListAsync();
      Assert.Equal([PermissionCodes.SalesRegister], codes);
    }
  }

  private static void AssertLacksLoyaltyPermission(AuthPayload payload)
  {
    Assert.DoesNotContain(PermissionCodes.LoyaltySettingsManage, payload.User.Permissions);
    var claims = new JwtSecurityTokenHandler().ReadJwtToken(payload.AccessToken).Claims
        .Where(claim => claim.Type == PermissionClaimTypes.Permission)
        .Select(claim => claim.Value);
    Assert.DoesNotContain(PermissionCodes.LoyaltySettingsManage, claims);
  }

  private static async Task RevokeAsync(IntegrationTestFactory factory, string roleName, string permissionCode)
  {
    using var scope = factory.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var normalizedName = roleName.ToUpperInvariant();
    dbContext.RolePermissions.Remove(await dbContext.RolePermissions.SingleAsync(
        grant => grant.Role.NormalizedName == normalizedName && grant.Permission.Code == permissionCode));
    await dbContext.SaveChangesAsync();
  }

  private static Task<List<string>> GrantedCodesAsync(AppDbContext dbContext, string roleName)
  {
    var normalizedName = roleName.ToUpperInvariant();
    return dbContext.RolePermissions
        .Where(grant => grant.Role.NormalizedName == normalizedName)
        .Select(grant => grant.Permission.Code)
        .ToListAsync();
  }

  private static async Task<AuthPayload> RegisterAdminAsync(IntegrationTestFactory factory, HttpClient client, string email)
  {
    client.DefaultRequestHeaders.Authorization = null;
    using (var register = await client.PostAsJsonAsync(
        "/api/v1/auth/register", new RegisterRequest("Admin Persistencia", email, DefaultPassword, "3001112233")))
    {
      Assert.Equal(HttpStatusCode.Created, register.StatusCode);
    }

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
    return (await login.Content.ReadFromJsonAsync<AuthPayload>())!;
  }

  private static async Task<HttpStatusCode> GetStatusAsync(HttpClient client, string accessToken, string url)
  {
    AuthenticateAs(client, accessToken);
    using var response = await client.GetAsync(url);
    return response.StatusCode;
  }

  private static void AuthenticateAs(HttpClient client, string accessToken)
  {
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
  }

  private sealed record AuthPayload(string AccessToken, string RefreshToken, AuthUserResponse User);
}
