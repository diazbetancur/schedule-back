using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.PublicContent;
using Barbershop.Application.Staff;
using Barbershop.Application.Staff.Admin;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Staff;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "AdminAsProfessionalHttpIntegration")]
public sealed class AdminAsProfessionalHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public AdminAsProfessionalHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task AdminWithoutProfile_CanSelfActivate_AndBecomesPubliclyBookable()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAdminAsync(context.Client, context.Factory, "self-activate");

    AuthenticateAs(context.Client, admin.AccessToken);

    using var response = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/staff/me",
        new EnableProfessionalProfileRequest("Owner Barber", null));

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    var payload = await response.Content.ReadFromJsonAsync<StaffManagementView>();
    Assert.NotNull(payload);
    Assert.True(payload!.IsActive);
    Assert.Equal("Owner Barber", payload.DisplayName);
    Assert.Equal(30, payload.DefaultAppointmentDurationMinutes);

    context.Client.DefaultRequestHeaders.Authorization = null;

    using var publicResponse = await context.Client.GetAsync("/api/v1/public/staff");

    Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);

    var publicPayload = await publicResponse.Content.ReadFromJsonAsync<List<PublicStaffListItemResponse>>();
    Assert.NotNull(publicPayload);
    Assert.Contains(publicPayload!, item => item.StaffProfileId == payload.StaffProfileId);
  }

  [Fact]
  public async Task SelfActivate_SecondAttempt_ReturnsConflict()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAdminAsync(context.Client, context.Factory, "conflict");

    AuthenticateAs(context.Client, admin.AccessToken);

    using var firstResponse = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/staff/me",
        new EnableProfessionalProfileRequest("First Activation", null));

    Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

    using var secondResponse = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/staff/me",
        new EnableProfessionalProfileRequest("Second Activation", null));

    Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
  }

  [Fact]
  public async Task SelfActivate_AsPlainCustomer_ReturnsForbidden()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var customer = await RegisterCustomerAsync(context.Client, "forbidden");

    AuthenticateAs(context.Client, customer.AccessToken);

    using var response = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/staff/me",
        new EnableProfessionalProfileRequest("Should Not Work", null));

    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
  }

  [Fact]
  public async Task SelfActivate_Unauthenticated_ReturnsUnauthorized()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    using var response = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/staff/me",
        new EnableProfessionalProfileRequest("No Auth", null));

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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

  /// <summary>
  /// Registers a plain customer, then promotes the underlying user to the Admin role directly
  /// via the DB context (there is no admin-registration endpoint and the admin seeder is disabled
  /// in tests), then logs in again so the returned token carries the Admin role claim.
  /// </summary>
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

  private static async Task WithScopeAsync(IntegrationTestFactory factory, Func<IServiceProvider, Task> action)
  {
    using var scope = factory.Services.CreateScope();
    await action(scope.ServiceProvider);
  }

  private static void AuthenticateAs(HttpClient client, string accessToken)
  {
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
  }

  private static string UniqueEmail(string prefix)
  {
    return $"{prefix}-{Guid.NewGuid():N}@example.com";
  }

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
