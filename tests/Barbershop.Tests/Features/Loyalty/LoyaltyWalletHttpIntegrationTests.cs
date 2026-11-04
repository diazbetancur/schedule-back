using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Loyalty.Wallet;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Domain.Common;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Services;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Barbershop.Tests.Features.Loyalty;

/// <summary>
/// The wallet over real HTTP, JWT and PostgreSQL: the income link that credits a membership needs the critical
/// memberships.payments.confirm permission and adjustments need loyalty.points.adjust, both re-checked against the
/// database with the same token; customers see only their own points; merges go through their preview token.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "LoyaltyWalletHttpIntegration")]
public sealed class LoyaltyWalletHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";
  private const string Terms = "La membresía es personal e intransferible.";

  private readonly DateOnly _today = DateOnly.FromDateTime(BogotaClock.ToLocal(DateTime.UtcNow));
  private readonly PostgresContainerFixture _postgres;

  public LoyaltyWalletHttpIntegrationTests(PostgresContainerFixture postgres) => _postgres = postgres;

  [Fact]
  public async Task LinkingAnIncome_CreditsTheMembershipOnce_AndNeedsTheCriticalPaymentPermission_RevokedWithTheSameToken()
  {
    if (!_postgres.IsAvailable) return; // Only with PostgresContainerFixture.AllowSkipVariable set on purpose.
    using var factory = new IntegrationTestFactory(_postgres.ConnectionString);
    await factory.ResetDatabaseAsync();
    using var client = factory.CreateClient();

    var admin = await RegisterAdminAsync(factory, client, "pagos");
    await EnableProgramAsync(factory);
    var customer = await CreateCustomerAsync(client, admin.AccessToken, "Carla Cliente");
    var membership = await SellAsync(factory, client, admin.AccessToken, customer.Id);
    var incomeId = await AddIncomeAsync(factory, admin.User.Id);

    await SetAdminPermissionAsync(factory, PermissionCodes.MembershipPaymentsConfirm, granted: false);
    using (var denied = await client.PutAsJsonAsync($"/api/v1/admin/memberships/{membership.Id}/income-link", new MembershipIncomeLinkRequest(incomeId)))
    {
      Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    // Unlinking and the rest of the membership flow keep their own policies.
    Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/admin/memberships/{membership.Id}")).StatusCode);

    await SetAdminPermissionAsync(factory, PermissionCodes.MembershipPaymentsConfirm, granted: true);
    using (var linked = await client.PutAsJsonAsync($"/api/v1/admin/memberships/{membership.Id}/income-link", new MembershipIncomeLinkRequest(incomeId)))
    {
      Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
      var view = (await linked.Content.ReadFromJsonAsync<CustomerMembershipDetailView>())!;
      Assert.Equal(("Accrued", 40), (view.Loyalty!.Decision, view.Loyalty.AccruedPoints));
    }

    var wallet = (await client.GetFromJsonAsync<LoyaltyWalletView>($"/api/v1/admin/customers/{customer.Id}/loyalty"))!;
    Assert.Equal(40, wallet.AvailablePoints);
  }

  [Fact]
  public async Task Adjustments_NeedTheCriticalPermission_TheCustomerSeesOnlyTheirPoints_AndMergesNeedAFreshPreview()
  {
    if (!_postgres.IsAvailable) return;
    using var factory = new IntegrationTestFactory(_postgres.ConnectionString);
    await factory.ResetDatabaseAsync();
    using var client = factory.CreateClient();

    var admin = await RegisterAdminAsync(factory, client, "ajustes");
    var customerAccount = await RegisterAsync(client, "Carla Cliente", UniqueEmail("carla"));
    AuthenticateAs(client, customerAccount.AccessToken);
    using (var own = await client.PostAsJsonAsync("/api/v1/customer/customer-profile",
        new OwnCustomerProfileCreateRequestShape("Carla Cliente", "3001234567", customerAccount.User.Email)))
    {
      Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    AuthenticateAs(client, admin.AccessToken);
    var mine = Assert.Single((await client.GetFromJsonAsync<List<CustomerListItem>>("/api/v1/admin/customers?q=Carla Cliente"))!);
    var other = await CreateCustomerAsync(client, admin.AccessToken, "Otra Persona");

    var credit = new LoyaltyAdjustmentRequest("Credit", 30, "Compensación por demora", Guid.NewGuid());
    using (var credited = await client.PostAsJsonAsync($"/api/v1/admin/customers/{mine.Id}/loyalty/adjustments", credit))
    {
      Assert.Equal(HttpStatusCode.OK, credited.StatusCode);
    }

    using (var otherCredit = await client.PostAsJsonAsync($"/api/v1/admin/customers/{other.Id}/loyalty/adjustments",
        new LoyaltyAdjustmentRequest("Credit", 99, "Bono de otra persona", Guid.NewGuid())))
    {
      Assert.Equal(HttpStatusCode.OK, otherCredit.StatusCode);
    }

    await SetAdminPermissionAsync(factory, PermissionCodes.LoyaltyPointsAdjust, granted: false);
    using (var denied = await client.PostAsJsonAsync($"/api/v1/admin/customers/{mine.Id}/loyalty/adjustments",
        new LoyaltyAdjustmentRequest("Debit", 5, "Sin permiso", Guid.NewGuid())))
    {
      Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/admin/customers/{mine.Id}/loyalty")).StatusCode);

    AuthenticateAs(client, customerAccount.AccessToken);
    var myPoints = (await client.GetFromJsonAsync<MyLoyaltyView>("/api/v1/customer/loyalty"))!;
    Assert.Equal((true, 30), (myPoints.HasProfile, myPoints.AvailablePoints));
    Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/admin/customers/{other.Id}/loyalty")).StatusCode);

    // A merge needs the preview's token; a change after the preview makes it stale.
    AuthenticateAs(client, admin.AccessToken);
    await SetAdminPermissionAsync(factory, PermissionCodes.LoyaltyPointsAdjust, granted: true);
    var preview = (await client.GetFromJsonAsync<CustomerMergePreview>($"/api/v1/admin/customers/{mine.Id}/merge-preview?sourceCustomerId={other.Id}"))!;
    Assert.Equal((99, 30, 129), (preview.Loyalty!.SourceAvailablePoints, preview.Loyalty.TargetAvailablePoints, preview.Loyalty.ResultAvailablePoints));
    using (await client.PostAsJsonAsync($"/api/v1/admin/customers/{other.Id}/loyalty/adjustments",
        new LoyaltyAdjustmentRequest("Debit", 9, "Corrección", Guid.NewGuid())))
    {
    }

    using (var stale = await client.PostAsJsonAsync($"/api/v1/admin/customers/{mine.Id}/merge", new CustomerMergeRequest(other.Id, preview.PreviewToken)))
    {
      Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    var fresh = (await client.GetFromJsonAsync<CustomerMergePreview>($"/api/v1/admin/customers/{mine.Id}/merge-preview?sourceCustomerId={other.Id}"))!;
    using (var merged = await client.PostAsJsonAsync($"/api/v1/admin/customers/{mine.Id}/merge", new CustomerMergeRequest(other.Id, fresh.PreviewToken)))
    {
      Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
    }

    AuthenticateAs(client, customerAccount.AccessToken);
    Assert.Equal(120, (await client.GetFromJsonAsync<MyLoyaltyView>("/api/v1/customer/loyalty"))!.AvailablePoints);
  }

  // ── Helpers ────────────────────────────────────────────────────────────────────────────────

  private sealed record OwnCustomerProfileCreateRequestShape(string FullName, string Phone, string Email);

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

  private async Task<CustomerMembershipDetailView> SellAsync(IntegrationTestFactory factory, HttpClient client, string adminToken, Guid customerId)
  {
    Guid serviceId;
    using (var scope = factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var service = new Service("Corte", 25000, DateTime.UtcNow, 30);
      db.Services.Add(service);
      await db.SaveChangesAsync();
      serviceId = service.Id;
    }

    AuthenticateAs(client, adminToken);
    using var plan = await client.PostAsJsonAsync("/api/v1/admin/membership-plans",
        new MembershipPlanUpsertRequest("Oro", null, 250000, "Pool", 10, 90, Terms, null, [new(serviceId, null)], [], 40));
    Assert.Equal(HttpStatusCode.Created, plan.StatusCode);
    var planId = (await plan.Content.ReadFromJsonAsync<MembershipPlanView>())!.Id;
    using var sale = await client.PostAsJsonAsync($"/api/v1/admin/customers/{customerId}/memberships", new MembershipSaleRequest(planId, _today, ActivateNow: true));
    Assert.Equal(HttpStatusCode.Created, sale.StatusCode);
    var membership = (await sale.Content.ReadFromJsonAsync<CustomerMembershipDetailView>())!;
    Assert.Equal((40, null), (membership.Loyalty!.PointsSnapshot, membership.Loyalty.Decision));
    return membership;
  }

  private async Task<Guid> AddIncomeAsync(IntegrationTestFactory factory, Guid adminId)
  {
    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var income = IncomeEntry.CreateMembershipSale(250000, _today, adminId, DateTime.UtcNow);
    db.IncomeEntries.Add(income);
    await db.SaveChangesAsync();
    return income.Id;
  }

  private static async Task<CustomerDetailView> CreateCustomerAsync(HttpClient client, string adminToken, string name)
  {
    AuthenticateAs(client, adminToken);
    using var response = await client.PostAsJsonAsync("/api/v1/admin/customers", new CustomerCreateRequest(name, "3001234567", UniqueEmail("cliente")));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<CustomerDetailView>())!;
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
