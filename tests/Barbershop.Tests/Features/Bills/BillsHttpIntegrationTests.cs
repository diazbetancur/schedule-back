using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Authorization;
using Barbershop.Application.Bills;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Bills;

/// <summary>
/// ADR-0020 stages 2 and 3 over HTTP: Staff opens, closes and charges by default but neither discounts, approves nor
/// voids; approving is critical (a revocation applies to the same token); completing an appointment opens its bill
/// (BUG-092); the permissions of the Staff role can be edited (BUG-091). One user per role (sign-in limit per IP).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "BillsHttpIntegration")]
public sealed class BillsHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";
  private static readonly Guid Cash = new("5d0c3a0e-6a4b-4f5e-9b0a-1c2d3e4f5a01");

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public BillsHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task Staff_OpensClosesAndCharges_ButNeitherDiscountsApprovesNorVoids_AndTheAdminApproves()
  {
    using var factory = await CreateFactoryAsync();
    if (factory is null) return;
    using var client = factory.CreateClient();
    var admin = await RegisterAdminAsync(factory, client, "bills");
    var (staff, staffProfileId) = await RegisterStaffAsync(factory, client, "bills");
    var customer = await RegisterAsync(client, "Cliente Común", UniqueEmail("customer"));
    var rateId = await SeedRateAsync(factory);

    AuthenticateAs(client, customer.AccessToken);
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.GetAsync("/api/v1/bills")));

    AuthenticateAs(client, staff.AccessToken);
    var bill = await ReadAsync<BillView>(client.PostAsJsonAsync("/api/v1/bills", new BillOpenRequest(Guid.NewGuid(), CustomerName: "Cliente de paso")));
    bill = await ReadAsync<BillView>(client.PostAsJsonAsync($"/api/v1/bills/{bill.Id}/lines", new BillLineRequest(
        bill.Version, "Service", staffProfileId, ServiceCatalogDefaults.CorteGeneralServiceId, rateId)));
    Assert.False(bill.Actions.CanDiscount);
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.PostAsJsonAsync($"/api/v1/bills/{bill.Id}/lines",
        new BillLineRequest(bill.Version, "Discount", Amount: 1000, Reason: "Cliente frecuente"))));
    bill = await ReadAsync<BillView>(client.PostAsJsonAsync($"/api/v1/bills/{bill.Id}/close", new BillCloseRequest(bill.Version, Cash)));
    Assert.Equal(("Closed", "C-000001", 25000), (bill.Status, bill.Number, bill.Total));

    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.GetAsync($"/api/v1/bills/{bill.Id}/approval-preview")));
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.PostAsJsonAsync(
        $"/api/v1/bills/{bill.Id}/approve", new BillApproveRequest(bill.Version, Guid.NewGuid()))));
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.PostAsJsonAsync(
        $"/api/v1/bills/{bill.Id}/void", new BillReasonRequest(bill.Version, "Error de registro"))));

    AuthenticateAs(client, admin.AccessToken);
    var preview = await client.GetFromJsonAsync<BillApprovalPreviewView>($"/api/v1/bills/{bill.Id}/approval-preview");
    Assert.Equal(15000, Assert.Single(preview!.Lines).ProfessionalAmount);
    var approved = await ReadAsync<BillView>(client.PostAsJsonAsync($"/api/v1/bills/{bill.Id}/approve", new BillApproveRequest(bill.Version, Guid.NewGuid())));
    Assert.Equal("Approved", approved.Status);
    Assert.True(approved.Actions.CanUnapprove);
    var report = await client.GetFromJsonAsync<Barbershop.Application.Finance.Admin.ReportSummaryView>("/api/v1/admin/reports/summary");
    Assert.Equal(25000, report!.TotalIncome);

    // Stage 5: the cash closing is for the administration; the receipts are the customer's own (none linked here).
    var cash = await client.GetFromJsonAsync<CashClosingView>("/api/v1/admin/reports/cash");
    Assert.Equal((1, 25000L), (cash!.BillCount, cash.Total));
    // Every account is also a customer: the admin only sees their own receipts (none) and never someone else's.
    Assert.Empty((await client.GetPageItemsAsync<MyBillSummaryView>("/api/v1/customer/bills"))!);
    Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client.GetAsync($"/api/v1/customer/bills/{bill.Id}")));
    AuthenticateAs(client, customer.AccessToken);
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.GetAsync("/api/v1/admin/reports/cash")));
    Assert.Empty((await client.GetPageItemsAsync<MyBillSummaryView>("/api/v1/customer/bills"))!);
    Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client.GetAsync($"/api/v1/customer/bills/{bill.Id}")));
    AuthenticateAs(client, staff.AccessToken);
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.GetAsync("/api/v1/admin/reports/cash")));
    AuthenticateAs(client, admin.AccessToken);

    // Stage 4: bills.unapprove is critical and Staff does not hold it; the reason is required.
    AuthenticateAs(client, staff.AccessToken);
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.PostAsJsonAsync(
        $"/api/v1/bills/{bill.Id}/unapprove", new BillUnapproveRequest(approved.Version, Guid.NewGuid(), "Corrección"))));
    AuthenticateAs(client, admin.AccessToken);
    Assert.Equal(HttpStatusCode.UnprocessableEntity, await StatusAsync(client.PostAsJsonAsync(
        $"/api/v1/bills/{bill.Id}/unapprove", new BillUnapproveRequest(approved.Version, Guid.NewGuid(), " "))));
    var unapproved = await ReadAsync<BillView>(client.PostAsJsonAsync(
        $"/api/v1/bills/{bill.Id}/unapprove", new BillUnapproveRequest(approved.Version, Guid.NewGuid(), "Se cobró de más")));
    Assert.Equal(("Unapproved", "Se cobró de más"), (unapproved.Status, unapproved.LastUnapprovalReason));
    // The period of the service keeps its income; today carries the counter-entry. Same day here: they net to 0.
    var corrected = await client.GetFromJsonAsync<Barbershop.Application.Finance.Admin.ReportSummaryView>("/api/v1/admin/reports/summary");
    Assert.Equal(0, corrected!.TotalIncome);
  }

  [Fact]
  public async Task BillsApprove_IsCritical_ARevocationAppliesToTheSameToken()
  {
    using var factory = await CreateFactoryAsync();
    if (factory is null) return;
    using var client = factory.CreateClient();
    var admin = await RegisterAdminAsync(factory, client, "approver");
    var staffProfileId = await SeedStaffProfileAsync(factory);
    var rateId = await SeedRateAsync(factory);
    var permissions = await GetPermissionsAsync(client, admin.AccessToken);
    var role = await CreateRoleAsync(client, admin.AccessToken, "Aprobación de cuentas", permissions[PermissionCodes.BillsApprove]);
    var approver = await CreateUserWithRolesAsync(client, admin.AccessToken, "approver", role.Id);

    AuthenticateAs(client, admin.AccessToken);
    var bill = await ReadAsync<BillView>(client.PostAsJsonAsync("/api/v1/bills",
        new BillOpenRequest(Guid.NewGuid(), CustomerName: "Cliente de paso", StaffProfileId: staffProfileId)));
    bill = await ReadAsync<BillView>(client.PostAsJsonAsync($"/api/v1/bills/{bill.Id}/lines", new BillLineRequest(
        bill.Version, "Service", staffProfileId, ServiceCatalogDefaults.CorteGeneralServiceId, rateId)));
    bill = await ReadAsync<BillView>(client.PostAsJsonAsync($"/api/v1/bills/{bill.Id}/close", new BillCloseRequest(bill.Version, Cash)));

    AuthenticateAs(client, approver.AccessToken);
    Assert.Equal(HttpStatusCode.OK, await StatusAsync(client.GetAsync($"/api/v1/bills/{bill.Id}/approval-preview")));
    Assert.Equal(HttpStatusCode.OK, await StatusAsync(client.GetAsync("/api/v1/bills?status=Closed")));

    AuthenticateAs(client, admin.AccessToken);
    using (var update = await client.PutAsJsonAsync($"/api/v1/admin/roles/{role.Id}", new RoleUpdateRequest("Aprobación de cuentas", [])))
    {
      Assert.Equal(HttpStatusCode.OK, update.StatusCode);
    }

    AuthenticateAs(client, approver.AccessToken);
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.PostAsJsonAsync(
        $"/api/v1/bills/{bill.Id}/approve", new BillApproveRequest(bill.Version, Guid.NewGuid()))));
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.GetAsync("/api/v1/bills")));
  }

  [Fact]
  public async Task Bug092_CompletingAnAppointmentOpensItsBill_AndThePendingViewListsIt()
  {
    using var factory = await CreateFactoryAsync();
    if (factory is null) return;
    using var client = factory.CreateClient();
    var (staff, staffProfileId) = await RegisterStaffAsync(factory, client, "complete");
    var (appointmentId, legacyId) = await SeedAppointmentsAsync(factory, staffProfileId);

    AuthenticateAs(client, staff.AccessToken);
    using (var complete = await client.PatchAsJsonAsync(
               $"/api/v1/staff/appointments/{appointmentId}/status", new { Status = "Completed" }))
    {
      Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
    }

    var pending = await client.GetFromJsonAsync<BillPendingView>("/api/v1/bills/pending");
    var opened = Assert.Single(pending!.OpenBills);
    Assert.Equal((appointmentId, "Cliente Agenda"), (opened.AppointmentId!.Value, opened.CustomerName));
    // An appointment completed before the bills existed, with nothing registered, is listed to open its bill.
    Assert.Equal(legacyId, Assert.Single(pending.AppointmentsWithoutBill).AppointmentId);
    Assert.Equal(2, (await client.GetFromJsonAsync<BillCountView>("/api/v1/bills/pending/count"))!.Count);

    var bill = await client.GetFromJsonAsync<BillView>($"/api/v1/bills/{opened.Id}");
    var line = Assert.Single(bill!.Lines);
    Assert.Equal(("Service", ServiceCatalogDefaults.CorteGeneralServiceId, (Guid?)null), (line.Type, line.GeneralServiceId!.Value, line.ServiceId));
  }

  [Fact]
  public async Task Bug091_TheStaffRolePermissionsCanBeEdited_ButNotItsNameNorTheAdminRole()
  {
    using var factory = await CreateFactoryAsync();
    if (factory is null) return;
    using var client = factory.CreateClient();
    var admin = await RegisterAdminAsync(factory, client, "roles");
    var permissions = await GetPermissionsAsync(client, admin.AccessToken);
    var roles = await client.GetFromJsonAsync<List<RoleView>>("/api/v1/admin/roles");
    var staffRole = roles!.Single(role => role.Name == RoleNames.Staff);
    var adminRole = roles.Single(role => role.Name == RoleNames.Admin);
    Assert.True(staffRole.CanEditPermissions);
    Assert.False(adminRole.CanEditPermissions);
    Assert.DoesNotContain("sales.register", permissions.Keys);

    using (var update = await client.PutAsJsonAsync($"/api/v1/admin/roles/{staffRole.Id}",
               new RoleUpdateRequest(RoleNames.Staff, [permissions[PermissionCodes.BillsOpen], permissions[PermissionCodes.BillsClose]])))
    {
      Assert.Equal(HttpStatusCode.OK, update.StatusCode);
      var updated = await update.Content.ReadFromJsonAsync<RoleView>();
      Assert.Equal([PermissionCodes.BillsClose, PermissionCodes.BillsOpen], updated!.Permissions.Select(permission => permission.Code));
    }

    Assert.Equal(HttpStatusCode.Conflict, await StatusAsync(client.PutAsJsonAsync($"/api/v1/admin/roles/{staffRole.Id}",
        new RoleUpdateRequest("Barberos", [permissions[PermissionCodes.BillsOpen]]))));
    Assert.Equal(HttpStatusCode.Conflict, await StatusAsync(client.PutAsJsonAsync($"/api/v1/admin/roles/{adminRole.Id}",
        new RoleUpdateRequest(RoleNames.Admin, []))));
  }

  // ── Helpers ────────────────────────────────────────────────────────────────────────────────

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

  /// <summary>A professional who never signs in (keeps the test under the sign-in limit per IP).</summary>
  private static async Task<Guid> SeedStaffProfileAsync(IntegrationTestFactory factory)
  {
    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var user = new User("Barbero sin sesión", UniqueEmail("staff-seed"), "hash", DateTime.UtcNow);
    var profile = new StaffProfile(user.Id, "Barbero sin sesión", 30, DateTime.UtcNow);
    db.Users.Add(user);
    db.StaffProfiles.Add(profile);
    await db.SaveChangesAsync();
    return profile.Id;
  }

  private static async Task<Guid> SeedRateAsync(IntegrationTestFactory factory)
  {
    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var rate = new Service("Corte clásico", 25000, DateTime.UtcNow, 40);
    db.Services.Add(rate);
    await db.SaveChangesAsync();
    return rate.Id;
  }

  private static async Task<(Guid Today, Guid Legacy)> SeedAppointmentsAsync(IntegrationTestFactory factory, Guid staffProfileId)
  {
    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var reservation = new AppointmentServiceReservation(ServiceCatalogDefaults.CorteGeneralServiceId, "Corte", 30);
    var today = Appointment.ForService(
        staffProfileId, "Cliente Agenda", DateTime.UtcNow.AddMinutes(-30), reservation, AppointmentStatus.Confirmed, AppointmentSource.Manual, DateTime.UtcNow);
    var legacy = Appointment.ForService(
        staffProfileId, "Cliente Anterior", DateTime.UtcNow.AddDays(-2), reservation, AppointmentStatus.Completed, AppointmentSource.Manual, DateTime.UtcNow);
    db.Appointments.AddRange(today, legacy);
    await db.SaveChangesAsync();
    return (today.Id, legacy.Id);
  }

  private static async Task<T> ReadAsync<T>(Task<HttpResponseMessage> request)
  {
    using var response = await request;
    Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    return (await response.Content.ReadFromJsonAsync<T>())!;
  }

  private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> request)
  {
    using var response = await request;
    return response.StatusCode;
  }

  private static async Task<IReadOnlyDictionary<string, Guid>> GetPermissionsAsync(HttpClient client, string adminToken)
  {
    AuthenticateAs(client, adminToken);
    var permissions = await client.GetFromJsonAsync<List<PermissionView>>("/api/v1/admin/permissions");
    return permissions!.ToDictionary(permission => permission.Code, permission => permission.Id, StringComparer.Ordinal);
  }

  private static async Task<RoleView> CreateRoleAsync(HttpClient client, string adminToken, string name, params Guid[] permissionIds)
  {
    AuthenticateAs(client, adminToken);
    using var response = await client.PostAsJsonAsync("/api/v1/admin/roles", new RoleCreateRequest(name, permissionIds));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<RoleView>())!;
  }

  private static async Task<AuthPayload> CreateUserWithRolesAsync(HttpClient client, string adminToken, string label, params Guid[] roleIds)
  {
    var email = UniqueEmail(label);
    var registered = await RegisterAsync(client, $"User {label}", email);
    AuthenticateAs(client, adminToken);
    using var assign = await client.PatchAsJsonAsync($"/api/v1/admin/users/{registered.User.Id}/roles", new { RoleIds = roleIds });
    Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
    return await LoginAsync(client, email);
  }

  private static async Task<(AuthPayload Session, Guid StaffProfileId)> RegisterStaffAsync(IntegrationTestFactory factory, HttpClient client, string label)
  {
    var email = UniqueEmail($"staff-{label}");
    var registered = await RegisterAsync(client, $"Barbero {label}", email);
    Guid staffProfileId;
    using (var scope = factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var staffRole = await db.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Staff.ToUpperInvariant());
      db.UserRoles.Add(new UserRole(registered.User.Id, staffRole.Id, DateTime.UtcNow));
      var profile = new StaffProfile(registered.User.Id, $"Barbero {label}", 30, DateTime.UtcNow);
      db.StaffProfiles.Add(profile);
      await db.SaveChangesAsync();
      staffProfileId = profile.Id;
    }

    return (await LoginAsync(client, email), staffProfileId);
  }

  private static async Task<AuthPayload> RegisterAsync(HttpClient client, string fullName, string email)
  {
    client.DefaultRequestHeaders.Authorization = null;
    using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(fullName, email, DefaultPassword, "3001112233"));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<AuthPayload>())!;
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
    var payload = (await login.Content.ReadFromJsonAsync<AuthPayload>())!;
    AuthenticateAs(client, payload.AccessToken);
    return payload;
  }

  private static void AuthenticateAs(HttpClient client, string accessToken)
      => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

  private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

  private sealed record AuthPayload(string AccessToken, AuthUserResponse User);
}
