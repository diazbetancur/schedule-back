using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using Barbershop.Application.Auth;
using Barbershop.Application.Authorization;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Authorization;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "AdminRolesHttpIntegration")]
public sealed class AdminRolesHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public AdminRolesHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task NonAdmin_CannotAccessRolesEndpoints_ReturnsForbidden()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var customer = await RegisterCustomerAsync(context.Client, "forbidden");
    AuthenticateAs(context.Client, customer.AccessToken);

    using var response = await context.Client.GetAsync("/api/v1/admin/roles");

    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
  }

  [Fact]
  public async Task Admin_CreatesRole_AssignsToUser_WhoGetsPermissionClaimOnNextLogin()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context.Client, context.Factory, "roles-flow");
    AuthenticateAs(context.Client, admin.AccessToken);

    using var permissionsResponse = await context.Client.GetAsync("/api/v1/admin/permissions");
    Assert.Equal(HttpStatusCode.OK, permissionsResponse.StatusCode);
    var permissions = await permissionsResponse.Content.ReadFromJsonAsync<List<PermissionView>>();
    Assert.NotNull(permissions);
    var salesPermission = Assert.Single(permissions!, p => p.Code == PermissionCodes.SalesRegister);

    using var createRoleResponse = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/roles",
        new RoleCreateRequest("Vendedor", [salesPermission.Id]));
    Assert.Equal(HttpStatusCode.Created, createRoleResponse.StatusCode);
    var role = await createRoleResponse.Content.ReadFromJsonAsync<RoleView>();
    Assert.NotNull(role);

    var seller = await RegisterCustomerAsync(context.Client, "seller");

    using var assignResponse = await context.Client.PatchAsJsonAsync(
        $"/api/v1/admin/users/{seller.UserId}/roles",
        new { RoleIds = new[] { role!.Id } });
    Assert.Equal(HttpStatusCode.OK, assignResponse.StatusCode);

    context.Client.DefaultRequestHeaders.Authorization = null;
    using var loginResponse = await context.Client.PostAsJsonAsync(
        "/api/v1/auth/login",
        new LoginRequest(await GetEmailAsync(context.Factory, seller.UserId), DefaultPassword));
    Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

    var login = await loginResponse.Content.ReadFromJsonAsync<TokenResponse>();
    Assert.NotNull(login);
    var jwt = new JwtSecurityTokenHandler().ReadJwtToken(login!.AccessToken);
    Assert.Contains(jwt.Claims, c => c.Type == PermissionClaimTypes.Permission && c.Value == PermissionCodes.SalesRegister);
  }

  [Fact]
  public async Task Admin_CannotEditOrDeleteSystemRole_ReturnsConflict()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context.Client, context.Factory, "system-role-guard");
    AuthenticateAs(context.Client, admin.AccessToken);

    using var listResponse = await context.Client.GetAsync("/api/v1/admin/roles");
    var roles = await listResponse.Content.ReadFromJsonAsync<List<RoleView>>();
    var adminRole = Assert.Single(roles!, r => r.Name == RoleNames.Admin);

    using var updateResponse = await context.Client.PutAsJsonAsync(
        $"/api/v1/admin/roles/{adminRole.Id}",
        new RoleUpdateRequest("Administrador", []));
    Assert.Equal(HttpStatusCode.Conflict, updateResponse.StatusCode);

    using var deleteResponse = await context.Client.DeleteAsync($"/api/v1/admin/roles/{adminRole.Id}");
    Assert.Equal(HttpStatusCode.Conflict, deleteResponse.StatusCode);
  }

  [Fact]
  public async Task AssigningSystemRole_ViaCustomRolesEndpoint_ReturnsBadRequest()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context.Client, context.Factory, "assign-system-guard");
    AuthenticateAs(context.Client, admin.AccessToken);

    using var listResponse = await context.Client.GetAsync("/api/v1/admin/roles");
    var roles = await listResponse.Content.ReadFromJsonAsync<List<RoleView>>();
    var staffRole = Assert.Single(roles!, r => r.Name == RoleNames.Staff);

    var target = await RegisterCustomerAsync(context.Client, "assign-target");

    using var assignResponse = await context.Client.PatchAsJsonAsync(
        $"/api/v1/admin/users/{target.UserId}/roles",
        new { RoleIds = new[] { staffRole.Id } });

    Assert.Equal(HttpStatusCode.BadRequest, assignResponse.StatusCode);
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

    var client = factory.CreateClient();
    return new TestHttpContext(factory, client);
  }

  private static async Task<AuthSession> RegisterCustomerAsync(HttpClient client, string label)
  {
    var request = new RegisterRequest(
        FullName: $"Customer {label}",
        Email: UniqueEmail($"customer-{label}"),
        Password: DefaultPassword,
        PhoneNumber: null);

    using var response = await client.PostAsJsonAsync("/api/v1/auth/register", request);
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);

    var payload = await response.Content.ReadFromJsonAsync<TokenResponse>();
    Assert.NotNull(payload);

    return new AuthSession(payload!.User.Id, payload.AccessToken);
  }

  private static async Task<AuthSession> RegisterAdminAsync(HttpClient client, IntegrationTestFactory factory, string label)
  {
    var email = UniqueEmail($"admin-{label}");

    var registerRequest = new RegisterRequest(
        FullName: $"Admin {label}",
        Email: email,
        Password: DefaultPassword,
        PhoneNumber: null);

    using var registerResponse = await client.PostAsJsonAsync("/api/v1/auth/register", registerRequest);
    Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);

    await PromoteToAdminAsync(factory, email);

    var loginRequest = new LoginRequest(email, DefaultPassword);

    using var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", loginRequest);
    Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

    var payload = await loginResponse.Content.ReadFromJsonAsync<TokenResponse>();
    Assert.NotNull(payload);

    return new AuthSession(payload!.User.Id, payload.AccessToken);
  }

  private static async Task PromoteToAdminAsync(IntegrationTestFactory factory, string email)
  {
    await WithScopeAsync(factory, async serviceProvider =>
    {
      var dbContext = serviceProvider.GetRequiredService<AppDbContext>();

      var normalizedEmail = email.ToUpperInvariant();
      var user = await dbContext.Users
          .Include(candidate => candidate.UserRoles)
          .SingleAsync(candidate => candidate.NormalizedEmail == normalizedEmail);

      var adminRole = await dbContext.Roles
          .SingleAsync(role => role.NormalizedName == RoleNames.Admin.ToUpperInvariant());

      if (user.UserRoles.All(assignment => assignment.RoleId != adminRole.Id))
      {
        user.UserRoles.Add(new UserRole(user.Id, adminRole.Id, DateTime.UtcNow));
        await dbContext.SaveChangesAsync();
      }
    });
  }

  private static async Task<string> GetEmailAsync(IntegrationTestFactory factory, Guid userId)
  {
    string? email = null;
    await WithScopeAsync(factory, async serviceProvider =>
    {
      var dbContext = serviceProvider.GetRequiredService<AppDbContext>();
      email = await dbContext.Users.Where(u => u.Id == userId).Select(u => u.Email).SingleAsync();
    });
    return email!;
  }

  private static async Task WithScopeAsync(IntegrationTestFactory factory, Func<IServiceProvider, Task> action)
  {
    using var scope = factory.Services.CreateScope();
    await action(scope.ServiceProvider);
  }

  private static void AuthenticateAs(HttpClient client, string accessToken)
  {
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
  }

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
