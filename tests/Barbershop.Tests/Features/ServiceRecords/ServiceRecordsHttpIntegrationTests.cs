using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Application.ServiceRecords;
using Barbershop.Application.Services.Admin;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Common;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.ServiceRecords;

/// <summary>
/// Services performed over real HTTP, JWT and PostgreSQL: who may register and approve (service-records.approve is
/// critical and checked against the database), row locks, and that concurrent or retried confirmations create one
/// income or one membership consumption at most.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "ServiceRecordsHttpIntegration")]
public sealed class ServiceRecordsHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";
  private const string StaffUrl = "/api/v1/staff/service-records";
  private const string AdminUrl = "/api/v1/admin/service-records";
  private const string Terms = "La membresía es personal e intransferible.";

  private readonly DateOnly _today = DateOnly.FromDateTime(BogotaClock.ToLocal(DateTime.UtcNow));
  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public ServiceRecordsHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task OnlyProfessionalsRegister_OnlyCurrentApproversReview_AndARevocationAppliesToTheSameToken()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context, "permissions");
    var staff = await RegisterStaffAsync(context, "permissions");
    var customer = await RegisterAsync(context.Client, "Plain Customer", UniqueEmail("customer"));
    var haircut = await CreateServiceAsync(context.Client, admin.AccessToken, "Corte");

    Assert.Equal(HttpStatusCode.Forbidden, await PostStatusAsync(context.Client, customer.AccessToken, StaffUrl, RegisterRequest(haircut.Id)));
    Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(context.Client, staff.AccessToken, AdminUrl));
    Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(context.Client, customer.AccessToken, AdminUrl));
    context.Client.DefaultRequestHeaders.Authorization = null;
    using (var anonymous = await context.Client.GetAsync(AdminUrl))
    {
      Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    var first = await RegisterRecordsAsync(context.Client, staff.AccessToken, RegisterRequest(haircut.Id));
    var second = await RegisterRecordsAsync(context.Client, staff.AccessToken, RegisterRequest(haircut.Id));

    // The professional cannot confirm their own registration through the admin routes.
    Assert.Equal(HttpStatusCode.Forbidden,
        await PostStatusAsync(context.Client, staff.AccessToken, $"{AdminUrl}/{first[0].Id}/approve", Courtesy(first[0])));

    AuthenticateAs(context.Client, admin.AccessToken);
    var pending = await context.Client.GetFromJsonAsync<List<ServiceRecordView>>(AdminUrl);
    Assert.Equal(2, pending!.Count);
    Assert.Equal(HttpStatusCode.OK, await PostStatusAsync(context.Client, admin.AccessToken, $"{AdminUrl}/{first[0].Id}/approve", Courtesy(first[0])));

    await RevokeFromAdminRoleAsync(context, PermissionCodes.ServiceRecordsApprove);

    // Same admin token, which still carries the claim: the next review is refused.
    Assert.Equal(HttpStatusCode.Forbidden,
        await PostStatusAsync(context.Client, admin.AccessToken, $"{AdminUrl}/{second[0].Id}/approve", Courtesy(second[0])));
    Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(context.Client, admin.AccessToken, AdminUrl));

    // Registering is a separate permission and keeps working.
    Assert.Equal(HttpStatusCode.OK, await PostStatusAsync(context.Client, staff.AccessToken, StaffUrl, RegisterRequest(haircut.Id)));
  }

  [Fact]
  public async Task ConcurrentConfirmations_CreateOneIncome_RetriesReplay_AndDifferentDecisionsAreConflicts()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context, "concurrency");
    var staff = await RegisterStaffAsync(context, "concurrency");
    var haircut = await CreateServiceAsync(context.Client, admin.AccessToken, "Corte");
    var same = (await RegisterRecordsAsync(context.Client, staff.AccessToken, RegisterRequest(haircut.Id)))[0];
    var different = (await RegisterRecordsAsync(context.Client, staff.AccessToken, RegisterRequest(haircut.Id)))[0];

    AuthenticateAs(context.Client, admin.AccessToken);
    var sameStatuses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PostAsync(context.Client, $"{AdminUrl}/{same.Id}/approve", Paid(same, 25000))));
    Assert.All(sameStatuses, status => Assert.Equal(HttpStatusCode.OK, status));

    var differentStatuses = await Task.WhenAll(Enumerable.Range(1, 6)
        .Select(index => PostAsync(context.Client, $"{AdminUrl}/{different.Id}/approve", Paid(different, 10000 * index))));
    Assert.Single(differentStatuses, status => status == HttpStatusCode.OK);
    Assert.All(differentStatuses.Where(status => status != HttpStatusCode.OK), status => Assert.Equal(HttpStatusCode.Conflict, status));

    using var scope = context.Factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var incomes = await db.IncomeEntries.AsNoTracking().ToListAsync();
    Assert.Equal(2, incomes.Count);
    var records = await db.ServiceRecords.AsNoTracking().ToListAsync();
    Assert.Equal(incomes.Select(income => income.Id).Order(), records.Select(record => record.IncomeEntryId!.Value).Order());
  }

  [Fact]
  public async Task ConcurrentMembershipConfirmations_ConsumeOnce()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context, "membership");
    var staff = await RegisterStaffAsync(context, "membership");
    var haircut = await CreateServiceAsync(context.Client, admin.AccessToken, "Corte");
    var (customerId, membershipId) = await SellActiveMembershipAsync(context.Client, admin.AccessToken, haircut.Id);
    var record = (await RegisterRecordsAsync(context.Client, staff.AccessToken, RegisterRequest(haircut.Id)))[0];
    var request = new ServiceRecordApproveRequest(
        record.Version, "MembershipConsumption", haircut.Id, record.StaffProfileId, customerId, record.PerformedOn, null, membershipId, false, null);

    AuthenticateAs(context.Client, admin.AccessToken);
    var statuses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => PostAsync(context.Client, $"{AdminUrl}/{record.Id}/approve", request)));
    Assert.All(statuses, status => Assert.Equal(HttpStatusCode.OK, status));

    var membership = await context.Client.GetFromJsonAsync<CustomerMembershipDetailView>($"/api/v1/admin/memberships/{membershipId}");
    Assert.Equal(9, membership!.Allowances.Single().AvailableQuantity);
    Assert.Single(membership.Consumptions);

    using var scope = context.Factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    Assert.Equal(0, await db.IncomeEntries.CountAsync(income => income.Kind == Barbershop.Domain.Finance.IncomeKind.Service));
  }

  [Fact]
  public async Task ConcurrentRegistrations_SameKeyStoreOnce_AndTheSameAppointmentServiceOnlyOnce()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context, "registration");
    var staff = await RegisterStaffAsync(context, "registration");
    var haircut = await CreateServiceAsync(context.Client, admin.AccessToken, "Corte");
    var beard = await CreateServiceAsync(context.Client, admin.AccessToken, "Barba");

    AuthenticateAs(context.Client, staff.AccessToken);
    var sameKey = RegisterRequest(haircut.Id, beard.Id);
    var retried = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => PostAsync(context.Client, StaffUrl, sameKey)));
    Assert.All(retried, status => Assert.Equal(HttpStatusCode.OK, status));

    var appointmentId = await CreateAppointmentAsync(context, staff.User.Id);
    var racing = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
        PostAsync(context.Client, StaffUrl, new ServiceRecordRegisterRequest(appointmentId, null, null, [haircut.Id], null, Guid.NewGuid()))));
    Assert.Single(racing, status => status == HttpStatusCode.OK);
    Assert.All(racing.Where(status => status != HttpStatusCode.OK), status => Assert.Equal(HttpStatusCode.Conflict, status));

    var mine = await context.Client.GetFromJsonAsync<List<ServiceRecordView>>(StaffUrl);
    Assert.Equal(3, mine!.Count);
    Assert.Single(mine, record => record.AppointmentId == appointmentId);
  }

  [Fact]
  public async Task AnApproverWithoutTheAdminRole_CreatesTheCustomerWhileConfirming_AndStillCannotUseTheCustomersModule()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var staff = await RegisterStaffAsync(context, "customer");
    var approver = await RegisterWithCustomRoleAsync(context, "approver", PermissionCodes.ServiceRecordsApprove);
    var serviceId = await AddServiceAsync(context, "Corte");
    var record = (await RegisterRecordsAsync(context.Client, staff.AccessToken, RegisterRequest(serviceId)))[0];

    AuthenticateAs(context.Client, approver.AccessToken);
    var newEmail = UniqueEmail("nuevo");
    var newCustomer = new CustomerCreateRequest("Cliente Nuevo", "300 765 4321", newEmail);
    var duplicatesUrl = $"{AdminUrl}/customer-duplicates?email={Uri.EscapeDataString(newEmail)}&phone={Uri.EscapeDataString(newCustomer.Phone)}";
    Assert.Empty((await context.Client.GetFromJsonAsync<List<ServiceRecordCustomerDuplicate>>(duplicatesUrl))!);

    using var created = await context.Client.PostAsJsonAsync($"{AdminUrl}/customers", newCustomer);
    Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    var createdJson = await created.Content.ReadAsStringAsync();
    // Only what selects the profile: no email, full phone, events nor account data.
    Assert.DoesNotContain(newEmail, createdJson, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("3007654321", createdJson);
    Assert.DoesNotContain("events", createdJson, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("linkedAccount", createdJson, StringComparison.OrdinalIgnoreCase);
    var customer = (await created.Content.ReadFromJsonAsync<ServiceRecordCustomerOption>())!;
    Assert.Equal(("Cliente Nuevo", "•••4321"), (customer.FullName, customer.PhoneHint));

    // The same contact data now matches that profile, and an app account with the same email is reported without
    // identifying the user.
    var duplicatesJson = await context.Client.GetStringAsync(
        $"{AdminUrl}/customer-duplicates?email={Uri.EscapeDataString(approver.User.Email)}&phone={Uri.EscapeDataString(newCustomer.Phone)}");
    Assert.DoesNotContain(approver.User.Email, duplicatesJson, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain(approver.User.Id.ToString(), duplicatesJson, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("3007654321", duplicatesJson);
    Assert.DoesNotContain("isLinked", duplicatesJson, StringComparison.OrdinalIgnoreCase);
    var again = (await context.Client.GetFromJsonAsync<List<ServiceRecordCustomerDuplicate>>(
        $"{AdminUrl}/customer-duplicates?email={Uri.EscapeDataString(approver.User.Email)}&phone={Uri.EscapeDataString(newCustomer.Phone)}"))!;
    var asCustomer = Assert.Single(again, candidate => candidate.Kind == "customer");
    Assert.Equal((customer.Id, "•••4321"), (asCustomer.CustomerId!.Value, asCustomer.PhoneHint!));
    var asAccount = Assert.Single(again, candidate => candidate.Kind == "account");
    Assert.Null(asAccount.CustomerId);
    Assert.StartsWith(approver.User.Email[..1], asAccount.EmailHint);
    Assert.Contains("•••@", asAccount.EmailHint);

    // Searching while confirming returns the same reduced data.
    var optionsJson = await context.Client.GetStringAsync($"{AdminUrl}/customer-options?q=Cliente%20Nuevo");
    Assert.DoesNotContain(newEmail, optionsJson, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("3007654321", optionsJson);

    using var approve = await context.Client.PostAsJsonAsync($"{AdminUrl}/{record.Id}/approve", new ServiceRecordApproveRequest(
        record.Version, "Paid", serviceId, record.StaffProfileId, customer.Id, record.PerformedOn, 25000, null, false, null));
    Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
    Assert.Equal(customer.Id, (await approve.Content.ReadFromJsonAsync<ServiceRecordView>())!.Approval!.CustomerId);

    // The profile was not linked to any account.
    using (var scope = context.Factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      Assert.Null((await db.Customers.AsNoTracking().SingleAsync(profile => profile.Id == customer.Id)).UserId);
    }

    // The customers module keeps requiring the Admin role.
    Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(context.Client, approver.AccessToken, "/api/v1/admin/customers"));
    Assert.Equal(HttpStatusCode.Forbidden, await PostStatusAsync(context.Client, staff.AccessToken, $"{AdminUrl}/customers", newCustomer));
  }

  [Fact]
  public async Task AnAdministratorWithAProfessionalProfile_RegistersWithoutTheStaffRole()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context, "professional");
    var serviceId = await AddServiceAsync(context, "Corte");

    AuthenticateAs(context.Client, admin.AccessToken);
    var before = await context.Client.GetFromJsonAsync<ServiceRecordEligibilityView>($"{StaffUrl}/eligibility");
    Assert.False(before!.HasStaffProfile);
    Assert.Equal(HttpStatusCode.Forbidden, await PostStatusAsync(context.Client, admin.AccessToken, StaffUrl, RegisterRequest(serviceId)));

    using (var scope = context.Factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      db.StaffProfiles.Add(new StaffProfile(admin.User.Id, "Admin Barbero", 30, DateTime.UtcNow));
      await db.SaveChangesAsync();
    }

    Assert.True((await context.Client.GetFromJsonAsync<ServiceRecordEligibilityView>($"{StaffUrl}/eligibility"))!.HasStaffProfile);
    Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(context.Client, admin.AccessToken, $"{StaffUrl}/appointments"));
    var records = await RegisterRecordsAsync(context.Client, admin.AccessToken, RegisterRequest(serviceId));
    Assert.Equal("Admin Barbero", records[0].StaffName);
    // The Staff-only agenda stays as it was.
    Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(context.Client, admin.AccessToken, "/api/v1/staff/appointments"));
  }

  // ── Helpers ────────────────────────────────────────────────────────────────────────────────

  private ServiceRecordRegisterRequest RegisterRequest(params Guid[] serviceIds)
      => new(null, _today, "Cliente de paso", serviceIds, null, Guid.NewGuid());

  private static ServiceRecordApproveRequest Courtesy(ServiceRecordView record)
      => new(record.Version, "Courtesy", record.ServiceId, record.StaffProfileId, null, record.PerformedOn, null, null, false, null);

  private static ServiceRecordApproveRequest Paid(ServiceRecordView record, int amount)
      => new(record.Version, "Paid", record.ServiceId, record.StaffProfileId, null, record.PerformedOn, amount, null, false, null);

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

  private static async Task<IReadOnlyList<ServiceRecordView>> RegisterRecordsAsync(HttpClient client, string token, ServiceRecordRegisterRequest request)
  {
    AuthenticateAs(client, token);
    using var response = await client.PostAsJsonAsync(StaffUrl, request);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<List<ServiceRecordView>>())!;
  }

  private static async Task<HttpStatusCode> PostAsync(HttpClient client, string url, object body)
  {
    using var response = await client.PostAsJsonAsync(url, body);
    return response.StatusCode;
  }

  private static async Task<HttpStatusCode> PostStatusAsync(HttpClient client, string token, string url, object body)
  {
    AuthenticateAs(client, token);
    return await PostAsync(client, url, body);
  }

  private static async Task<HttpStatusCode> GetStatusAsync(HttpClient client, string token, string url)
  {
    AuthenticateAs(client, token);
    using var response = await client.GetAsync(url);
    return response.StatusCode;
  }

  private static async Task<ServiceView> CreateServiceAsync(HttpClient client, string adminToken, string name)
  {
    AuthenticateAs(client, adminToken);
    using var response = await client.PostAsJsonAsync("/api/v1/admin/services", new ServiceCreateRequest(name, 25000, 30));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<ServiceView>())!;
  }

  private async Task<(Guid CustomerId, Guid MembershipId)> SellActiveMembershipAsync(HttpClient client, string adminToken, Guid serviceId)
  {
    AuthenticateAs(client, adminToken);
    using var plan = await client.PostAsJsonAsync("/api/v1/admin/membership-plans", new MembershipPlanUpsertRequest(
        "Oro", null, 250000, "Pool", 10, 90, Terms, null, [new(serviceId, null)], []));
    Assert.Equal(HttpStatusCode.Created, plan.StatusCode);
    var planId = (await plan.Content.ReadFromJsonAsync<MembershipPlanView>())!.Id;

    using var customer = await client.PostAsJsonAsync("/api/v1/admin/customers",
        new CustomerCreateRequest("Dueño Membresía", "3001234567", UniqueEmail("owner")));
    Assert.Equal(HttpStatusCode.Created, customer.StatusCode);
    var customerId = (await customer.Content.ReadFromJsonAsync<CustomerDetailView>())!.Id;

    using var sale = await client.PostAsJsonAsync($"/api/v1/admin/customers/{customerId}/memberships",
        new MembershipSaleRequest(planId, _today, ActivateNow: true));
    Assert.Equal(HttpStatusCode.Created, sale.StatusCode);
    return (customerId, (await sale.Content.ReadFromJsonAsync<CustomerMembershipDetailView>())!.Id);
  }

  private static async Task<Guid> CreateAppointmentAsync(TestHttpContext context, Guid staffUserId)
  {
    using var scope = context.Factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var staffProfileId = await db.StaffProfiles.Where(staff => staff.UserId == staffUserId).Select(staff => staff.Id).SingleAsync();
    var startsAt = DateTime.UtcNow.AddHours(-2);
    var appointment = new Appointment(staffProfileId, "Cliente Cita", startsAt, startsAt.AddMinutes(30),
        AppointmentStatus.Completed, AppointmentSource.Manual, DateTime.UtcNow);
    db.Appointments.Add(appointment);
    await db.SaveChangesAsync();
    return appointment.Id;
  }

  private static async Task RevokeFromAdminRoleAsync(TestHttpContext context, string permissionCode)
  {
    using var scope = context.Factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.RolePermissions.Remove(await db.RolePermissions.SingleAsync(grant =>
        grant.Role.NormalizedName == RoleNames.Admin.ToUpperInvariant() && grant.Permission.Code == permissionCode));
    await db.SaveChangesAsync();
  }

  private static async Task<Guid> AddServiceAsync(TestHttpContext context, string name)
  {
    using var scope = context.Factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var service = new Barbershop.Domain.Services.Service(name, 25000, DateTime.UtcNow, 30);
    db.Services.Add(service);
    await db.SaveChangesAsync();
    return service.Id;
  }

  /// <summary>Registers an account with a custom role holding only the given permissions, and signs in again.</summary>
  private static async Task<AuthPayload> RegisterWithCustomRoleAsync(TestHttpContext context, string label, params string[] permissionCodes)
  {
    var email = UniqueEmail(label);
    await RegisterAsync(context.Client, $"Usuario {label}", email);
    using (var scope = context.Factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var user = await db.Users.Include(candidate => candidate.UserRoles).SingleAsync(candidate => candidate.NormalizedEmail == email.ToUpperInvariant());
      var role = new Role($"Rol {label}");
      foreach (var permission in await db.Permissions.Where(permission => permissionCodes.Contains(permission.Code)).ToListAsync())
      {
        role.AddPermission(permission.Id, DateTime.UtcNow);
      }

      db.Roles.Add(role);
      user.UserRoles.Add(new UserRole(user.Id, role.Id, DateTime.UtcNow));
      await db.SaveChangesAsync();
    }

    return await LoginAsync(context.Client, email);
  }

  /// <summary>Registers an account, makes it a professional (Staff role and profile) and signs in again.</summary>
  private static async Task<AuthPayload> RegisterStaffAsync(TestHttpContext context, string label)
  {
    var email = UniqueEmail($"staff-{label}");
    await RegisterAsync(context.Client, $"Barbero {label}", email);
    using (var scope = context.Factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var user = await db.Users.Include(candidate => candidate.UserRoles).SingleAsync(candidate => candidate.NormalizedEmail == email.ToUpperInvariant());
      var staffRole = await db.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Staff.ToUpperInvariant());
      user.UserRoles.Add(new UserRole(user.Id, staffRole.Id, DateTime.UtcNow));
      db.StaffProfiles.Add(new StaffProfile(user.Id, $"Barbero {label}", 30, DateTime.UtcNow));
      await db.SaveChangesAsync();
    }

    return await LoginAsync(context.Client, email);
  }

  private static async Task<AuthPayload> RegisterAdminAsync(TestHttpContext context, string label)
  {
    var email = UniqueEmail($"admin-{label}");
    await RegisterAsync(context.Client, $"Admin {label}", email);
    using (var scope = context.Factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var user = await db.Users.Include(candidate => candidate.UserRoles).SingleAsync(candidate => candidate.NormalizedEmail == email.ToUpperInvariant());
      var adminRole = await db.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Admin.ToUpperInvariant());
      user.UserRoles.Add(new UserRole(user.Id, adminRole.Id, DateTime.UtcNow));
      await db.SaveChangesAsync();
    }

    return await LoginAsync(context.Client, email);
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
