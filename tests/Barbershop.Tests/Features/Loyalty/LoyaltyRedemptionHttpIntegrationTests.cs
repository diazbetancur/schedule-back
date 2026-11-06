using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Loyalty.Redemptions;
using Barbershop.Application.Loyalty.Wallet;
using Barbershop.Application.ServiceRecords;
using Barbershop.Domain.Common;
using Barbershop.Domain.ServiceRecords;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Barbershop.Tests.Features.Loyalty;

/// <summary>
/// Rewards and redemptions over real HTTP, JWT and PostgreSQL (ADR-0015): the catalog needs loyalty.rewards.manage;
/// approving needs loyalty.redemptions.approve and delivering loyalty.redemptions.deliver, both critical and re-checked
/// against the database with the same token, also when a service record is confirmed as a reward; customers only see
/// and cancel their own redemptions.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "LoyaltyRedemptionHttpIntegration")]
public sealed class LoyaltyRedemptionHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";

  private readonly DateOnly _today = DateOnly.FromDateTime(BogotaClock.ToLocal(DateTime.UtcNow));
  private readonly PostgresContainerFixture _postgres;

  public LoyaltyRedemptionHttpIntegrationTests(PostgresContainerFixture postgres) => _postgres = postgres;

  [Fact]
  public async Task TheCustomerRequests_ApprovalAndDeliveryNeedTheirCriticalPermissions_RevokedWithTheSameToken()
  {
    if (!_postgres.IsAvailable) return; // Only with PostgresContainerFixture.AllowSkipVariable set on purpose.
    using var factory = new IntegrationTestFactory(_postgres.ConnectionString);
    await factory.ResetDatabaseAsync();
    using var client = factory.CreateClient();

    var admin = await RegisterAdminAsync(factory, client, "canjes");
    await EnableProgramAsync(factory);
    var (customer, customerId) = await RegisterCustomerWithProfileAsync(client, admin.AccessToken, "Carla Cliente");
    var (stranger, _) = await RegisterCustomerWithProfileAsync(client, admin.AccessToken, "Otra Persona");
    await CreditAsync(client, admin.AccessToken, customerId, 50);

    AuthenticateAs(client, admin.AccessToken);
    using var created = await client.PostAsJsonAsync("/api/v1/admin/loyalty/rewards", new LoyaltyRewardUpsertRequest("Gorra", "Gorra de la barbería", "Other", null, 30));
    Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    var reward = (await created.Content.ReadFromJsonAsync<LoyaltyRewardView>())!;

    // The customer sees the catalog and requests; the points move from available to reserved.
    AuthenticateAs(client, customer.AccessToken);
    var catalog = (await client.GetFromJsonAsync<MyLoyaltyCatalogView>("/api/v1/customer/loyalty/rewards"))!;
    Assert.True(Assert.Single(catalog.Rewards).CanAfford);
    using var requestedResponse = await client.PostAsJsonAsync("/api/v1/customer/loyalty/redemptions", new LoyaltyRedemptionRequest(reward.Id, Guid.NewGuid()));
    Assert.Equal(HttpStatusCode.OK, requestedResponse.StatusCode);
    var requested = (await requestedResponse.Content.ReadFromJsonAsync<MyLoyaltyRedemptionView>())!;
    var points = (await client.GetFromJsonAsync<MyLoyaltyView>("/api/v1/customer/loyalty"))!;
    Assert.Equal((20, 30), (points.AvailablePoints, points.ReservedPoints));
    Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/admin/loyalty/redemptions")).StatusCode);

    // Another customer can neither see nor cancel it.
    AuthenticateAs(client, stranger.AccessToken);
    Assert.Empty((await client.GetFromJsonAsync<List<MyLoyaltyRedemptionView>>("/api/v1/customer/loyalty/redemptions"))!);
    using (var foreign = await client.PostAsJsonAsync($"/api/v1/customer/loyalty/redemptions/{requested.Id}/cancel", new MyLoyaltyRedemptionCancelRequest(null)))
    {
      Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    // Approving needs loyalty.redemptions.approve, checked against the database with the same token.
    AuthenticateAs(client, admin.AccessToken);
    var pending = Assert.Single((await client.GetFromJsonAsync<List<LoyaltyRedemptionView>>("/api/v1/admin/loyalty/redemptions?status=Requested"))!);
    await SetAdminPermissionAsync(factory, PermissionCodes.LoyaltyRedemptionsApprove, granted: false);
    using (var denied = await client.PostAsJsonAsync($"/api/v1/admin/loyalty/redemptions/{pending.Id}/approve", new LoyaltyRedemptionDecisionRequest(pending.Version)))
    {
      Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    await SetAdminPermissionAsync(factory, PermissionCodes.LoyaltyRedemptionsApprove, granted: true);
    using var approvedResponse = await client.PostAsJsonAsync($"/api/v1/admin/loyalty/redemptions/{pending.Id}/approve", new LoyaltyRedemptionDecisionRequest(pending.Version));
    Assert.Equal(HttpStatusCode.OK, approvedResponse.StatusCode);
    var approved = (await approvedResponse.Content.ReadFromJsonAsync<LoyaltyRedemptionView>())!;
    Assert.Equal((_today, _today.AddDays(29)), (approved.RequestedOn, approved.DeliverBy!.Value));

    // Delivering needs loyalty.redemptions.deliver, also checked against the database.
    await SetAdminPermissionAsync(factory, PermissionCodes.LoyaltyRedemptionsDeliver, granted: false);
    Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/admin/loyalty/redemptions/deliveries")).StatusCode);
    using (var denied = await client.PostAsJsonAsync($"/api/v1/admin/loyalty/redemptions/{pending.Id}/deliver", new LoyaltyRedemptionDecisionRequest(approved.Version)))
    {
      Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    await SetAdminPermissionAsync(factory, PermissionCodes.LoyaltyRedemptionsDeliver, granted: true);
    Assert.Single((await client.GetFromJsonAsync<List<LoyaltyRedemptionView>>("/api/v1/admin/loyalty/redemptions/deliveries"))!);
    using (var delivered = await client.PostAsJsonAsync($"/api/v1/admin/loyalty/redemptions/{pending.Id}/deliver", new LoyaltyRedemptionDecisionRequest(approved.Version)))
    {
      Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
    }

    AuthenticateAs(client, customer.AccessToken);
    Assert.Equal("Delivered", Assert.Single((await client.GetFromJsonAsync<List<MyLoyaltyRedemptionView>>("/api/v1/customer/loyalty/redemptions"))!).Status);
    points = (await client.GetFromJsonAsync<MyLoyaltyView>("/api/v1/customer/loyalty"))!;
    Assert.Equal((20, 0), (points.AvailablePoints, points.ReservedPoints));
    Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/admin/loyalty/rewards")).StatusCode);
  }

  [Fact]
  public async Task ConfirmingAServiceRecordAsReward_NeedsTheDeliverPermission_AndNoDirectDeliveryExists()
  {
    if (!_postgres.IsAvailable) return;
    using var factory = new IntegrationTestFactory(_postgres.ConnectionString);
    await factory.ResetDatabaseAsync();
    using var client = factory.CreateClient();

    var admin = await RegisterAdminAsync(factory, client, "servicio");
    await EnableProgramAsync(factory);
    var (_, customerId) = await RegisterCustomerWithProfileAsync(client, admin.AccessToken, "Carla Cliente");
    await CreditAsync(client, admin.AccessToken, customerId, 50);
    var (serviceId, staffProfileId) = await SeedServiceAndBarberAsync(factory);

    AuthenticateAs(client, admin.AccessToken);
    using var created = await client.PostAsJsonAsync("/api/v1/admin/loyalty/rewards", new LoyaltyRewardUpsertRequest("Corte gratis", null, "Service", serviceId, 40));
    var reward = (await created.Content.ReadFromJsonAsync<LoyaltyRewardView>())!;
    using var counter = await client.PostAsJsonAsync($"/api/v1/admin/loyalty/redemptions/customers/{customerId}",
        new LoyaltyCounterRedemptionRequest(reward.Id, InPersonVerified: true, ApproveNow: true, Guid.NewGuid()));
    Assert.Equal(HttpStatusCode.OK, counter.StatusCode);
    var redemption = (await counter.Content.ReadFromJsonAsync<LoyaltyRedemptionView>())!;
    Assert.Equal(("Approved", "Counter"), (redemption.Status, redemption.Channel));

    using (var direct = await client.PostAsJsonAsync($"/api/v1/admin/loyalty/redemptions/{redemption.Id}/deliver", new LoyaltyRedemptionDecisionRequest(redemption.Version)))
    {
      Assert.Equal(HttpStatusCode.Conflict, direct.StatusCode);
    }

    var record = await SeedPendingRecordAsync(factory, serviceId, staffProfileId, admin.User.Id);
    var options = (await client.GetFromJsonAsync<List<ServiceRecordRedemptionOption>>(
        $"/api/v1/admin/service-records/redemption-options?customerId={customerId}&serviceId={serviceId}"))!;
    Assert.Equal(redemption.Id, Assert.Single(options).Id);
    var approval = new ServiceRecordApproveRequest(record.Version, "LoyaltyReward", serviceId, staffProfileId, customerId, _today, null, null, false, null, redemption.Id);

    await SetAdminPermissionAsync(factory, PermissionCodes.LoyaltyRedemptionsDeliver, granted: false);
    using (var denied = await client.PostAsJsonAsync($"/api/v1/admin/service-records/{record.Id}/approve", approval))
    {
      Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    await SetAdminPermissionAsync(factory, PermissionCodes.LoyaltyRedemptionsDeliver, granted: true);
    using var confirmed = await client.PostAsJsonAsync($"/api/v1/admin/service-records/{record.Id}/approve", approval);
    Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
    var view = (await confirmed.Content.ReadFromJsonAsync<ServiceRecordView>())!;
    Assert.Equal(("LoyaltyReward", 17500), (view.Approval!.OperationType, view.Approval.RewardProfessionalCommission!.Value));

    var summary = (await client.GetFromJsonAsync<Barbershop.Application.Finance.Admin.ReportSummaryView>($"/api/v1/admin/reports/summary?from={_today:yyyy-MM-dd}&to={_today:yyyy-MM-dd}"))!;
    Assert.Equal((0L, 17500L), (summary.TotalIncome, summary.RewardCommissionsTotal));
  }

  // ── Helpers ────────────────────────────────────────────────────────────────────────────────

  private sealed record OwnCustomerProfileCreateRequestShape(string FullName, string Phone, string Email);

  private async Task<(AuthPayload Account, Guid CustomerId)> RegisterCustomerWithProfileAsync(HttpClient client, string adminToken, string name)
  {
    var account = await RegisterAsync(client, name, UniqueEmail("cliente"));
    AuthenticateAs(client, account.AccessToken);
    using (var own = await client.PostAsJsonAsync("/api/v1/customer/customer-profile", new OwnCustomerProfileCreateRequestShape(name, "3001234567", account.User.Email)))
    {
      Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    AuthenticateAs(client, adminToken);
    var profile = Assert.Single((await client.GetFromJsonAsync<List<CustomerListItem>>($"/api/v1/admin/customers?q={Uri.EscapeDataString(name)}"))!);
    return (account, profile.Id);
  }

  private static async Task CreditAsync(HttpClient client, string adminToken, Guid customerId, int points)
  {
    AuthenticateAs(client, adminToken);
    using var credited = await client.PostAsJsonAsync($"/api/v1/admin/customers/{customerId}/loyalty/adjustments",
        new LoyaltyAdjustmentRequest("Credit", points, "Puntos de prueba", Guid.NewGuid()));
    Assert.Equal(HttpStatusCode.OK, credited.StatusCode);
  }

  private static async Task<(Guid ServiceId, Guid StaffProfileId)> SeedServiceAndBarberAsync(IntegrationTestFactory factory)
  {
    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var service = new Service("Corte", 25000, DateTime.UtcNow, 30);
    var barberUser = new User("Alex Barbero", UniqueEmail("barbero"), "hash", DateTime.UtcNow);
    var barber = new StaffProfile(barberUser.Id, "Alex", 30, DateTime.UtcNow);
    db.Services.Add(service);
    db.Users.Add(barberUser);
    db.StaffProfiles.Add(barber);
    await db.SaveChangesAsync();
    return (service.Id, barber.Id);
  }

  private async Task<ServiceRecord> SeedPendingRecordAsync(IntegrationTestFactory factory, Guid serviceId, Guid staffProfileId, Guid adminId)
  {
    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var record = new ServiceRecord(Guid.NewGuid(), 0, null, staffProfileId, serviceId, "Corte", _today, null, "Carla", null, adminId, DateTime.UtcNow);
    db.ServiceRecords.Add(record);
    await db.SaveChangesAsync();
    return await db.ServiceRecords.AsNoTracking().SingleAsync(candidate => candidate.Id == record.Id);
  }

  private static async Task EnableProgramAsync(IntegrationTestFactory factory)
  {
    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var settings = await db.LoyaltySettings.SingleAsync();
    settings.Update(true, 365, (await db.Users.FirstAsync()).Id, DateTime.UtcNow);
    await db.SaveChangesAsync();
  }

  /// <summary>Grants or revokes a permission on the Admin role directly in the database (the API cannot edit system roles).</summary>
  private static async Task SetAdminPermissionAsync(IntegrationTestFactory factory, string code, bool granted)
  {
    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var role = await db.Roles.Include(candidate => candidate.RolePermissions)
        .SingleAsync(candidate => candidate.NormalizedName == RoleNames.Admin.ToUpperInvariant());
    var permission = await db.Permissions.SingleAsync(candidate => candidate.Code == code);
    var grant = role.RolePermissions.SingleOrDefault(candidate => candidate.PermissionId == permission.Id);
    if (granted && grant is null)
    {
      db.RolePermissions.Add(new RolePermission(role.Id, permission.Id, DateTime.UtcNow));
    }
    else if (!granted && grant is not null)
    {
      db.RolePermissions.Remove(grant);
    }

    await db.SaveChangesAsync();
  }

  private static async Task<AuthPayload> RegisterAdminAsync(IntegrationTestFactory factory, HttpClient client, string label)
  {
    var email = UniqueEmail($"admin-{label}");
    await RegisterAsync(client, $"Admin {label}", email);
    using (var scope = factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var user = await db.Users.Include(candidate => candidate.UserRoles).SingleAsync(candidate => candidate.NormalizedEmail == email.ToUpperInvariant());
      var adminRole = await db.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Admin.ToUpperInvariant());
      user.UserRoles.Add(new UserRole(user.Id, adminRole.Id, DateTime.UtcNow));
      await db.SaveChangesAsync();
    }

    var login = await LoginAsync(client, email);
    AuthenticateAs(client, login.AccessToken);
    return login;
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
}
