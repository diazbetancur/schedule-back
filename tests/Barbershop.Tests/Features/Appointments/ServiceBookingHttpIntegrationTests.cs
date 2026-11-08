using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Barbershop.Application.Appointments;
using Barbershop.Application.Auth;
using Barbershop.Application.Availability;
using Barbershop.Application.PublicContent;
using Barbershop.Application.Services.Admin;
using Barbershop.Application.Staff;
using Barbershop.Application.Staff.Admin;
using Barbershop.Domain.Common;
using Barbershop.Domain.Scheduling;
using Barbershop.Domain.Services;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Appointments;

/// <summary>
/// ADR-0016 B2/B3 over HTTP: the offering per professional (admin), the public profile without prices, availability
/// and booking by service, and the manual appointments of F3.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "ServiceBookingHttpIntegration")]
public sealed class ServiceBookingHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";
  private static readonly Guid Corte = ServiceCatalogDefaults.CorteGeneralServiceId;

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public ServiceBookingHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task Offerings_AreManagedByAdministration_AndThePublicProfileShowsThemWithoutPrices()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context, "offerings");
    AuthenticateAs(context.Client, admin.AccessToken);
    var staff = await CreateStaffAsync(context.Client, "offerings");
    var url = $"/api/v1/admin/staff/{staff.StaffProfileId}/offerings";

    // A professional created after the migration offers nothing until administration configures it.
    var initial = await context.Client.GetFromJsonAsync<StaffServiceOfferingsView>(url);
    Assert.Empty(initial!.Offerings);
    Assert.Equal(30, initial.DefaultAppointmentDurationMinutes);

    Assert.Equal(HttpStatusCode.NotFound, (await context.Client.GetAsync($"/api/v1/admin/staff/{Guid.NewGuid()}/offerings")).StatusCode);
    Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutOfferingsAsync(context.Client, url, (Corte, 5, true))).StatusCode);

    var color = await CreateGeneralServiceAsync(context.Client, "Color");
    using (var deactivate = await context.Client.PatchAsJsonAsync(
               $"/api/v1/admin/general-services/{color}/status", new GeneralServiceStatusUpdateRequest(false)))
    {
      Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
    }

    Assert.Equal(HttpStatusCode.Conflict, (await PutOfferingsAsync(context.Client, url, (Corte, 30, true), (color, 60, true))).StatusCode);

    using var saved = await PutOfferingsAsync(context.Client, url, (Corte, 40, true), (color, 60, false));
    Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    var view = await saved.Content.ReadFromJsonAsync<StaffServiceOfferingsView>();
    // Same sort order: by name ("Color" before "Corte").
    Assert.Equal([(color, 60, false), (Corte, 40, true)], view!.Offerings.Select(item => (item.GeneralServiceId, item.DurationMinutes, item.IsActive)));

    // Only administration manages offerings.
    var customer = await RegisterCustomerAsync(context.Client, "offerings");
    AuthenticateAs(context.Client, customer.AccessToken);
    Assert.Equal(HttpStatusCode.Forbidden, (await context.Client.GetAsync(url)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await PutOfferingsAsync(context.Client, url, (Corte, 30, true))).StatusCode);
    context.Client.DefaultRequestHeaders.Authorization = null;
    Assert.Equal(HttpStatusCode.Unauthorized, (await context.Client.GetAsync(url)).StatusCode);

    // The public profile shows the bookable offering only: name and duration, never a price.
    using var publicResponse = await context.Client.GetAsync($"/api/v1/public/staff/{staff.StaffProfileId}");
    Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
    var raw = await publicResponse.Content.ReadAsStringAsync();
    Assert.DoesNotContain("price", raw, StringComparison.OrdinalIgnoreCase);
    var profile = await publicResponse.Content.ReadFromJsonAsync<PublicStaffProfileResponse>();
    var service = Assert.Single(profile!.Services);
    Assert.Equal((Corte, "Corte", 40), (service.GeneralServiceId, service.Name, service.DurationMinutes));
  }

  [Fact]
  public async Task Customers_BookAServiceTheProfessionalOffers_OnlyInFreeSlotsOfItsDuration()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context, "booking");
    AuthenticateAs(context.Client, admin.AccessToken);
    var staff = await CreateStaffAsync(context.Client, "booking");
    var color = await CreateGeneralServiceAsync(context.Client, "Color");
    var beard = await CreateGeneralServiceAsync(context.Client, "Barba");
    Assert.Equal(HttpStatusCode.OK, (await PutOfferingsAsync(
        context.Client, $"/api/v1/admin/staff/{staff.StaffProfileId}/offerings", (Corte, 30, true), (color, 45, true))).StatusCode);
    var day = await AddRuleAsync(context, staff.StaffProfileId);
    var slotsUrl = $"/api/v1/public/staff/{staff.StaffProfileId}/availability/slots?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}";

    context.Client.DefaultRequestHeaders.Authorization = null;
    Assert.Equal(HttpStatusCode.UnprocessableEntity, (await context.Client.GetAsync(slotsUrl)).StatusCode);
    Assert.Equal(HttpStatusCode.UnprocessableEntity, (await context.Client.GetAsync($"{slotsUrl}&generalServiceId={beard}")).StatusCode);
    var colorSlots = await context.Client.GetFromJsonAsync<PublicAvailabilitySlotsResponse>($"{slotsUrl}&generalServiceId={color}");
    Assert.Equal((45, 15), (colorSlots!.SlotDurationMinutes, colorSlots.SlotStepMinutes));
    Assert.Equal(At(day, 9, 0), colorSlots.Slots[0].StartAtUtc);
    Assert.Equal(At(day, 9, 15), colorSlots.Slots[1].StartAtUtc);
    var fixedSlots = await context.Client.GetFromJsonAsync<PublicAvailabilitySlotsResponse>($"{slotsUrl}&durationMinutes=60");
    Assert.Equal(60, fixedSlots!.SlotDurationMinutes);

    var anonymous = await context.Client.PostAsJsonAsync("/api/v1/customer/appointments",
        new CustomerAppointmentCreateRequest(staff.StaffProfileId, color, At(day, 9, 0), null));
    Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

    var customer = await RegisterCustomerAsync(context.Client, "booking");
    AuthenticateAs(context.Client, customer.AccessToken);
    Assert.Equal(HttpStatusCode.UnprocessableEntity, (await BookAsync(context.Client, staff.StaffProfileId, Guid.Empty, At(day, 9, 0))).StatusCode);
    Assert.Equal(HttpStatusCode.Conflict, (await BookAsync(context.Client, staff.StaffProfileId, beard, At(day, 9, 0))).StatusCode);
    Assert.Equal(HttpStatusCode.Conflict, (await BookAsync(context.Client, staff.StaffProfileId, color, At(day, 9, 5))).StatusCode);

    using var booked = await BookAsync(context.Client, staff.StaffProfileId, color, At(day, 9, 0));
    Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
    var appointment = await booked.Content.ReadFromJsonAsync<AppointmentView>(Json);
    Assert.Equal((color, "Color", 45, At(day, 9, 45)),
        (appointment!.GeneralServiceId!.Value, appointment.GeneralServiceName, appointment.ReservedDurationMinutes!.Value, appointment.EndsAtUtc));

    // A Corte overlapping the color is a conflict; the slots of Corte skip the color's 45 minutes.
    Assert.Equal(HttpStatusCode.Conflict, (await BookAsync(context.Client, staff.StaffProfileId, Corte, At(day, 9, 30))).StatusCode);
    var corteSlots = await context.Client.GetFromJsonAsync<PublicAvailabilitySlotsResponse>($"{slotsUrl}&generalServiceId={Corte}");
    Assert.Equal(At(day, 9, 45), corteSlots!.Slots[0].StartAtUtc);

    // Rescheduling keeps service and duration.
    using var moved = await context.Client.PatchAsJsonAsync(
        $"/api/v1/customer/appointments/{appointment.Id}/reschedule", new CustomerAppointmentRescheduleRequest(At(day, 10, 15)));
    Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
    var movedView = await moved.Content.ReadFromJsonAsync<AppointmentView>(Json);
    Assert.Equal((color, 45, At(day, 11, 0)), (movedView!.GeneralServiceId!.Value, movedView.ReservedDurationMinutes!.Value, movedView.EndsAtUtc));
  }

  [Fact]
  public async Task ManualAppointments_RequireAnOfferedService_AndOnlyAdministrationChangesTheDuration()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context, "manual");
    AuthenticateAs(context.Client, admin.AccessToken);
    var staff = await CreateStaffAsync(context.Client, "manual");
    Assert.Equal(HttpStatusCode.OK, (await PutOfferingsAsync(
        context.Client, $"/api/v1/admin/staff/{staff.StaffProfileId}/offerings", (Corte, 30, true))).StatusCode);
    var day = await AddRuleAsync(context, staff.StaffProfileId);

    var adminOptions = await context.Client.GetFromJsonAsync<List<AppointmentServiceOption>>(
        $"/api/v1/admin/appointments/service-options?staffProfileId={staff.StaffProfileId}");
    Assert.Equal([(Corte, 30)], adminOptions!.Select(option => (option.GeneralServiceId, option.DurationMinutes)));

    using var adminCreated = await context.Client.PostAsJsonAsync("/api/v1/admin/appointments",
        new AdminManualAppointmentCreateRequest(staff.StaffProfileId, At(day, 9, 0), Corte, "Cliente admin", null, null, null, 50));
    Assert.Equal(HttpStatusCode.Created, adminCreated.StatusCode);
    var adminView = await adminCreated.Content.ReadFromJsonAsync<AppointmentView>(Json);
    Assert.Equal((50, At(day, 9, 50)), (adminView!.ReservedDurationMinutes!.Value, adminView.EndsAtUtc));

    var notOffered = await CreateGeneralServiceAsync(context.Client, "Barba");
    Assert.Equal(HttpStatusCode.Conflict, (await context.Client.PostAsJsonAsync("/api/v1/admin/appointments",
        new AdminManualAppointmentCreateRequest(staff.StaffProfileId, At(day, 11, 0), notOffered, "Cliente", null, null, null))).StatusCode);

    var staffSession = await LoginAsync(context.Client, staff.Email);
    AuthenticateAs(context.Client, staffSession.AccessToken);
    var ownOptions = await context.Client.GetFromJsonAsync<List<AppointmentServiceOption>>("/api/v1/staff/appointments/service-options");
    Assert.Single(ownOptions!);

    using var longer = await context.Client.PostAsJsonAsync("/api/v1/staff/appointments",
        new StaffManualAppointmentCreateRequest(At(day, 10, 0), Corte, "Cliente staff", null, null, null, 45));
    Assert.Equal(HttpStatusCode.UnprocessableEntity, longer.StatusCode);

    using var staffCreated = await context.Client.PostAsJsonAsync("/api/v1/staff/appointments",
        new StaffManualAppointmentCreateRequest(At(day, 10, 0), Corte, "Cliente staff", null, null, null));
    Assert.Equal(HttpStatusCode.Created, staffCreated.StatusCode);
    var staffView = await staffCreated.Content.ReadFromJsonAsync<AppointmentView>(Json);

    using var staffOverride = await context.Client.PutAsJsonAsync($"/api/v1/staff/appointments/{staffView!.Id}",
        new AppointmentUpdateRequest(At(day, 10, 30), "Cliente staff", null, null, null, null, 60));
    Assert.Equal(HttpStatusCode.UnprocessableEntity, staffOverride.StatusCode);

    // Staff cannot use the administration's options; customers cannot use the staff's.
    Assert.Equal(HttpStatusCode.Forbidden, (await context.Client.GetAsync(
        $"/api/v1/admin/appointments/service-options?staffProfileId={staff.StaffProfileId}")).StatusCode);
    var customer = await RegisterCustomerAsync(context.Client, "manual");
    AuthenticateAs(context.Client, customer.AccessToken);
    Assert.Equal(HttpStatusCode.Forbidden, (await context.Client.GetAsync("/api/v1/staff/appointments/service-options")).StatusCode);
    context.Client.DefaultRequestHeaders.Authorization = null;
    Assert.Equal(HttpStatusCode.Unauthorized, (await context.Client.GetAsync("/api/v1/staff/appointments/service-options")).StatusCode);
  }

  // ── Helpers ────────────────────────────────────────────────────────────────────────────────

  private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

  private static DateTime At(DateOnly day, int hour, int minute) => BogotaClock.ToUtc(day, new TimeOnly(hour, minute));

  private static Task<HttpResponseMessage> PutOfferingsAsync(
      HttpClient client, string url, params (Guid GeneralServiceId, int Minutes, bool IsActive)[] items)
      => client.PutAsJsonAsync(url, new StaffServiceOfferingsReplaceRequest(
          items.Select(item => new StaffServiceOfferingRequest(item.GeneralServiceId, item.Minutes, item.IsActive)).ToArray()));

  private static Task<HttpResponseMessage> BookAsync(HttpClient client, Guid staffProfileId, Guid generalServiceId, DateTime startsAtUtc)
      => client.PostAsJsonAsync("/api/v1/customer/appointments",
          new CustomerAppointmentCreateRequest(staffProfileId, generalServiceId, startsAtUtc, null));

  /// <summary>A 9:00-12:00 Bogotá rule on a day two days ahead; returns that day.</summary>
  private static async Task<DateOnly> AddRuleAsync(TestHttpContext context, Guid staffProfileId)
  {
    var day = DateOnly.FromDateTime(BogotaClock.ToLocal(DateTime.UtcNow)).AddDays(2);
    using var scope = context.Factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.StaffAvailabilityRules.Add(new StaffAvailabilityRule(staffProfileId, day.DayOfWeek, new TimeOnly(9, 0), new TimeOnly(12, 0)));
    await db.SaveChangesAsync();
    return day;
  }

  private static async Task<Guid> CreateGeneralServiceAsync(HttpClient client, string name)
  {
    using var response = await client.PostAsJsonAsync(
        "/api/v1/admin/general-services", new GeneralServiceCreateRequest(ServiceCatalogDefaults.BarberiaCategoryId, name));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<GeneralServiceView>())!.Id;
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

  private static async Task<(Guid StaffProfileId, string Email)> CreateStaffAsync(HttpClient client, string label)
  {
    var email = UniqueEmail($"staff-{label}");
    using var response = await client.PostAsJsonAsync("/api/v1/admin/staff", new AdminStaffCreateRequest(
        $"Staff {label}", email, $"Staff {label}", DefaultPassword, null, null, null, null, null, true));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    return ((await response.Content.ReadFromJsonAsync<StaffManagementView>())!.StaffProfileId, email);
  }

  private static async Task<AuthSession> RegisterCustomerAsync(HttpClient client, string label)
  {
    client.DefaultRequestHeaders.Authorization = null;
    using var response = await client.PostAsJsonAsync(
        "/api/v1/auth/register", new RegisterRequest($"Customer {label}", UniqueEmail($"customer-{label}"), DefaultPassword, null));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    var payload = await response.Content.ReadFromJsonAsync<TokenResponse>();
    return new AuthSession(payload!.User.Id, payload.AccessToken);
  }

  private static async Task<AuthSession> LoginAsync(HttpClient client, string email)
  {
    client.DefaultRequestHeaders.Authorization = null;
    using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, DefaultPassword));
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var payload = await response.Content.ReadFromJsonAsync<TokenResponse>();
    return new AuthSession(payload!.User.Id, payload.AccessToken);
  }

  private static async Task<AuthSession> RegisterAdminAsync(TestHttpContext context, string label)
  {
    var email = UniqueEmail($"admin-{label}");
    using (var register = await context.Client.PostAsJsonAsync(
               "/api/v1/auth/register", new RegisterRequest($"Admin {label}", email, DefaultPassword, null)))
    {
      Assert.Equal(HttpStatusCode.Created, register.StatusCode);
    }

    using (var scope = context.Factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var normalizedEmail = email.ToUpperInvariant();
      var user = await db.Users.Include(candidate => candidate.UserRoles).SingleAsync(candidate => candidate.NormalizedEmail == normalizedEmail);
      var adminRole = await db.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Admin.ToUpperInvariant());
      user.UserRoles.Add(new UserRole(user.Id, adminRole.Id, DateTime.UtcNow));
      await db.SaveChangesAsync();
    }

    return await LoginAsync(context.Client, email);
  }

  private static void AuthenticateAs(HttpClient client, string accessToken)
      => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

  private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

  private sealed record AuthSession(Guid UserId, string AccessToken);

  private sealed class TestHttpContext(IntegrationTestFactory factory, HttpClient client) : IDisposable
  {
    public IntegrationTestFactory Factory { get; } = factory;

    public HttpClient Client { get; } = client;

    public void Dispose()
    {
      Client.Dispose();
      Factory.Dispose();
    }
  }
}
