using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Auth;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "AccountSecurityHttpIntegration")]
public sealed class AccountSecurityHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public AccountSecurityHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task Register_WithEmailOfAccountDeactivatedByAdmin_ReturnsConflict_AndLeavesTheAccountAsItWas()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context.Client, context.Factory, "deactivate");
    var email = UniqueEmail("dormant");
    var victim = await RegisterAsync(context.Client, "Dormant Owner", email);

    AuthenticateAs(context.Client, admin.AccessToken);
    using (var deactivate = await context.Client.DeleteAsync($"/api/v1/admin/users/{victim.User.Id}"))
    {
      Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);
    }

    var before = await LoadUserAsync(context.Factory, victim.User.Id);
    context.Client.DefaultRequestHeaders.Authorization = null;

    using var reRegister = await context.Client.PostAsJsonAsync(
        "/api/v1/auth/register",
        new RegisterRequest("Intruder", email.ToUpperInvariant(), "Intruder123!", "3009998877"));
    Assert.Equal(HttpStatusCode.Conflict, reRegister.StatusCode);

    var after = await LoadUserAsync(context.Factory, victim.User.Id);
    Assert.False(after.IsActive);
    Assert.Equal(before.PasswordHash, after.PasswordHash);
    Assert.Equal("Dormant Owner", after.FullName);
    Assert.Equal(before.UpdatedAt, after.UpdatedAt);

    // The auth endpoints allow 5 credential requests per minute per client; this is the fifth.
    using var intruderLogin = await context.Client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "Intruder123!"));
    Assert.Equal(HttpStatusCode.Unauthorized, intruderLogin.StatusCode);
  }

  [Fact]
  public async Task Register_WithEmailOfActiveAccount_ReturnsConflict_WithTheSameResponseAsAnInactiveOne()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    // Accounts seeded straight into the disposable database, to stay under the auth rate limit.
    var activeEmail = UniqueEmail("active");
    var inactiveEmail = UniqueEmail("inactive");
    await SeedUserAsync(context.Factory, "Active Owner", activeEmail, isActive: true);
    await SeedUserAsync(context.Factory, "Inactive Owner", inactiveEmail, isActive: false);

    using var activeAttempt = await context.Client.PostAsJsonAsync(
        "/api/v1/auth/register", new RegisterRequest("Someone", activeEmail, "Other123!", null));
    using var inactiveAttempt = await context.Client.PostAsJsonAsync(
        "/api/v1/auth/register", new RegisterRequest("Someone", inactiveEmail, "Other123!", null));

    Assert.Equal(HttpStatusCode.Conflict, activeAttempt.StatusCode);
    Assert.Equal(HttpStatusCode.Conflict, inactiveAttempt.StatusCode);
    var activeProblem = await ReadProblemFieldsAsync(activeAttempt);
    var inactiveProblem = await ReadProblemFieldsAsync(inactiveAttempt);
    Assert.Equal(activeProblem, inactiveProblem);
  }

  [Fact]
  public async Task SimultaneousRegistrations_WithTheSameEmail_CreateOneAccount_AndAnswerTheRestWithConflict()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    // Five requests: exactly the auth limit per client, so none is turned away as 429.
    var email = UniqueEmail("simultaneous");
    var attempts = Enumerable.Range(1, 5)
        .Select(index => context.Client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest($"Person {index}", index % 2 == 0 ? email.ToUpperInvariant() : email, $"Secret{index}23!", null)))
        .ToArray();
    var responses = await Task.WhenAll(attempts);

    try
    {
      var statuses = responses.Select(response => response.StatusCode).ToArray();
      Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.Created));
      Assert.Equal(4, statuses.Count(status => status == HttpStatusCode.Conflict));

      var problems = await Task.WhenAll(responses
          .Where(response => response.StatusCode == HttpStatusCode.Conflict)
          .Select(ReadProblemFieldsAsync));
      Assert.Single(problems.Distinct(StringComparer.Ordinal));

      using var scope = context.Factory.Services.CreateScope();
      var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      Assert.Equal(1, await dbContext.Users.CountAsync(user => user.NormalizedEmail == email.ToUpperInvariant()));
    }
    finally
    {
      foreach (var response in responses)
      {
        response.Dispose();
      }
    }
  }

  [Fact]
  public async Task AuthResponses_ExposePermissions_ForAdminAndNoneForCustomers()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var customer = await RegisterAsync(context.Client, "Plain Customer", UniqueEmail("plain"));
    Assert.Empty(customer.User.Permissions);

    var admin = await RegisterAdminAsync(context.Client, context.Factory, "exposed");
    Assert.Equal(PermissionCodes.All.OrderBy(code => code, StringComparer.Ordinal), admin.User.Permissions);

    AuthenticateAs(context.Client, admin.AccessToken);
    var me = await context.Client.GetFromJsonAsync<AuthUserResponse>("/api/v1/auth/me");
    Assert.NotNull(me);
    Assert.Equal(PermissionCodes.All.OrderBy(code => code, StringComparer.Ordinal), me!.Permissions);
  }

  [Fact]
  public async Task Seed_GrantsAdminEverything_StaffOnlyServiceRegistration_AndCustomerNothing()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    await RegisterAsync(context.Client, "Seed Trigger", UniqueEmail("seed"));

    using var scope = context.Factory.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var roles = await dbContext.Roles
        .Include(role => role.RolePermissions)
        .ThenInclude(rolePermission => rolePermission.Permission)
        .AsNoTracking()
        .ToListAsync();

    var admin = roles.Single(role => role.Name == RoleNames.Admin);
    Assert.Equal(
        PermissionCodes.All.OrderBy(code => code, StringComparer.Ordinal),
        admin.RolePermissions.Select(rp => rp.Permission.Code).OrderBy(code => code, StringComparer.Ordinal));
    Assert.Equal(
        [PermissionCodes.ServiceRecordsRegister],
        roles.Single(role => role.Name == RoleNames.Staff).RolePermissions.Select(rp => rp.Permission.Code));
    Assert.Empty(roles.Single(role => role.Name == RoleNames.Customer).RolePermissions);
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

  private static async Task<AuthPayload> RegisterAsync(HttpClient client, string fullName, string email)
  {
    using var response = await client.PostAsJsonAsync(
        "/api/v1/auth/register", new RegisterRequest(fullName, email, DefaultPassword, "3001112233"));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);

    var payload = await response.Content.ReadFromJsonAsync<AuthPayload>();
    Assert.NotNull(payload);
    return payload!;
  }

  private static async Task<AuthPayload> RegisterAdminAsync(HttpClient client, IntegrationTestFactory factory, string label)
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

    using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, DefaultPassword));
    Assert.Equal(HttpStatusCode.OK, login.StatusCode);

    var payload = await login.Content.ReadFromJsonAsync<AuthPayload>();
    Assert.NotNull(payload);
    return payload!;
  }

  private static async Task SeedUserAsync(IntegrationTestFactory factory, string fullName, string email, bool isActive)
  {
    using var scope = factory.Services.CreateScope();
    var seeder = scope.ServiceProvider.GetRequiredService<Barbershop.Infrastructure.Identity.IIdentitySeedService>();
    await seeder.EnsureSeededAsync();

    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var user = new User(fullName, email, "not-a-real-hash", DateTime.UtcNow);
    var customerRole = await dbContext.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Customer.ToUpperInvariant());
    user.UserRoles.Add(new UserRole(user.Id, customerRole.Id, DateTime.UtcNow));
    if (!isActive)
    {
      user.Deactivate(DateTime.UtcNow);
    }

    dbContext.Users.Add(user);
    await dbContext.SaveChangesAsync();
  }

  private static async Task<User> LoadUserAsync(IntegrationTestFactory factory, Guid userId)
  {
    using var scope = factory.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    return await dbContext.Users.AsNoTracking().SingleAsync(user => user.Id == userId);
  }

  /// <summary>The fields a caller could use to tell the cases apart; per-request ids (trace, correlation) are left out.</summary>
  private static async Task<string> ReadProblemFieldsAsync(HttpResponseMessage response)
  {
    using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var root = document.RootElement;
    string Field(string name) => root.TryGetProperty(name, out var value) ? value.ToString() : "<missing>";
    return $"{Field("type")}|{Field("title")}|{Field("status")}|{Field("detail")}";
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
