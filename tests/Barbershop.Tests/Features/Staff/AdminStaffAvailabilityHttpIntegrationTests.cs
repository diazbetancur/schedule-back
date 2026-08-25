using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Availability;
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
[Trait("Category", "AdminStaffAvailabilityHttpIntegration")]
public sealed class AdminStaffAvailabilityHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public AdminStaffAvailabilityHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task ReplaceRulesAsAdmin_ForAnotherProfessional_PersistsRules()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context.Client, context.Factory, "avail-rules");
    AuthenticateAs(context.Client, admin.AccessToken);

    var staffProfileId = await CreateStaffAsync(context.Client, "avail-rules");

    var rules = new[]
    {
      new AvailabilityRuleRequest(2, new TimeOnly(9, 0), new TimeOnly(12, 0), true),
    };

    using var response = await context.Client.PutAsJsonAsync(
        $"/api/v1/admin/staff/{staffProfileId}/availability/rules", rules);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    var payload = await response.Content.ReadFromJsonAsync<List<AvailabilityRuleResponse>>();
    Assert.NotNull(payload);
    Assert.Single(payload!);
    Assert.Equal(2, payload![0].DayOfWeek);
    Assert.Equal(new TimeOnly(9, 0), payload[0].StartTime);

    using var getResponse = await context.Client.GetAsync($"/api/v1/admin/staff/{staffProfileId}/availability");
    Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    var summary = await getResponse.Content.ReadFromJsonAsync<AvailabilitySummaryResponse>();
    Assert.NotNull(summary);
    Assert.Single(summary!.Rules);
  }

  [Fact]
  public async Task UnavailablePeriodCrud_AsAdmin_ForAnotherProfessional_Works()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context.Client, context.Factory, "avail-periods");
    AuthenticateAs(context.Client, admin.AccessToken);

    var staffProfileId = await CreateStaffAsync(context.Client, "avail-periods");

    var start = DateTime.UtcNow.Date.AddDays(3).AddHours(9);
    var end = start.AddHours(2);

    using var createResponse = await context.Client.PostAsJsonAsync(
        $"/api/v1/admin/staff/{staffProfileId}/availability/unavailable-periods",
        new UnavailablePeriodCreateRequest(start, end, "Vacaciones"));

    Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
    var created = await createResponse.Content.ReadFromJsonAsync<UnavailablePeriodResponse>();
    Assert.NotNull(created);
    Assert.Equal("Vacaciones", created!.Reason);

    using var listResponse = await context.Client.GetAsync(
        $"/api/v1/admin/staff/{staffProfileId}/availability/unavailable-periods");
    Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
    var list = await listResponse.Content.ReadFromJsonAsync<List<UnavailablePeriodResponse>>();
    Assert.Contains(list!, item => item.Id == created.Id);

    using var updateResponse = await context.Client.PutAsJsonAsync(
        $"/api/v1/admin/staff/{staffProfileId}/availability/unavailable-periods/{created.Id}",
        new UnavailablePeriodUpdateRequest(start, end, "Vacaciones actualizadas"));
    Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
    var updated = await updateResponse.Content.ReadFromJsonAsync<UnavailablePeriodResponse>();
    Assert.Equal("Vacaciones actualizadas", updated!.Reason);

    using var deleteResponse = await context.Client.DeleteAsync(
        $"/api/v1/admin/staff/{staffProfileId}/availability/unavailable-periods/{created.Id}");
    Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
  }

  [Fact]
  public async Task GetAvailability_UnknownStaffProfileId_ReturnsNotFound()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context.Client, context.Factory, "avail-404");
    AuthenticateAs(context.Client, admin.AccessToken);

    using var response = await context.Client.GetAsync($"/api/v1/admin/staff/{Guid.NewGuid()}/availability");

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  [Fact]
  public async Task ReplaceRulesAsPlainCustomer_ReturnsForbidden()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context.Client, context.Factory, "avail-403-setup");
    AuthenticateAs(context.Client, admin.AccessToken);
    var staffProfileId = await CreateStaffAsync(context.Client, "avail-403");

    var customer = await RegisterCustomerAsync(context.Client, "avail-403");
    AuthenticateAs(context.Client, customer.AccessToken);

    var rules = new[] { new AvailabilityRuleRequest(1, new TimeOnly(9, 0), new TimeOnly(10, 0), true) };
    using var response = await context.Client.PutAsJsonAsync(
        $"/api/v1/admin/staff/{staffProfileId}/availability/rules", rules);

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

    var client = factory.CreateClient();
    return new TestHttpContext(factory, client);
  }

  private static async Task<Guid> CreateStaffAsync(HttpClient client, string label)
  {
    var request = new AdminStaffCreateRequest(
        FullName: $"Staff {label}",
        Email: UniqueEmail($"staff-{label}"),
        DisplayName: $"Staff {label}",
        InitialPassword: DefaultPassword,
        PhoneNumber: null,
        Bio: null,
        DefaultAppointmentDurationMinutes: null,
        PhotoMediaAssetId: null,
        TipsQrMediaAssetId: null,
        IsActive: true);

    using var response = await client.PostAsJsonAsync("/api/v1/admin/staff", request);
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);

    var payload = await response.Content.ReadFromJsonAsync<StaffManagementView>();
    Assert.NotNull(payload);
    return payload!.StaffProfileId;
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
    using var scope = factory.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

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
