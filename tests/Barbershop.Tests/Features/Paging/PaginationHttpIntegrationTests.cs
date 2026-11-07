using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Barbershop.Application.Appointments;
using Barbershop.Application.ServiceRecords;
using Barbershop.Application.Loyalty.Redemptions;
using Barbershop.Application.Customers.Claims;
using Barbershop.Application.Auth;
using Barbershop.Application.Common.Paging;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Finance.Admin;
using Barbershop.Application.Loyalty.Wallet;
using Barbershop.Application.Products;
using Barbershop.Application.Services.Admin;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.ServiceRecords;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.Common;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Products;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Paging;

/// <summary>
/// Server-side pagination and history filters (ADR-0016 B7) over real HTTP and PostgreSQL: page size limits (422),
/// totals that do not depend on the page, no duplicates or gaps between pages when sort dates tie, and every filter.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "PaginationHttpIntegration")]
public sealed class PaginationHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";
  // Statuses travel as names.
  private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

  private static readonly DateOnly Today = DateOnly.FromDateTime(BogotaClock.ToLocal(DateTime.UtcNow));

  private static readonly string[] AdminListUrls =
  [
    "/api/v1/admin/income",
    "/api/v1/admin/appointments",
    "/api/v1/admin/service-records",
    "/api/v1/admin/loyalty/redemptions",
    "/api/v1/admin/customers",
    "/api/v1/admin/services",
    "/api/v1/admin/products",
  ];

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public PaginationHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task EveryPaginatedList_RejectsAnOutOfRangePageOrPageSize_With422()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAdminAsync(context, "limits");
    var customerId = await SeedCustomerAsync(context.Factory, "Cliente Límites", admin.User.Id);
    var adminUrls = AdminListUrls
        .Append($"/api/v1/admin/customers/{customerId}/memberships")
        .Append($"/api/v1/admin/customers/{customerId}/loyalty/movements")
        .ToArray();

    AuthenticateAs(context.Client, admin.AccessToken);
    foreach (var url in adminUrls)
    {
      await AssertValidationAsync(context.Client, $"{url}?pageSize=101", "pageSize");
      await AssertValidationAsync(context.Client, $"{url}?pageSize=0", "pageSize");
      await AssertValidationAsync(context.Client, $"{url}?page=0", "page");
      using var maximum = await context.Client.GetAsync($"{url}?pageSize=100");
      Assert.True(maximum.StatusCode == HttpStatusCode.OK, $"{url} answered {maximum.StatusCode} for pageSize=100.");
    }

    var staff = await RegisterStaffAsync(context, "limits");
    AuthenticateAs(context.Client, staff.AccessToken);
    await AssertValidationAsync(context.Client, "/api/v1/staff/appointments?pageSize=101", "pageSize");

    var customer = await RegisterAsync(context.Client, "Cliente App", UniqueEmail("customer-limits"));
    AuthenticateAs(context.Client, customer.AccessToken);
    foreach (var url in new[] { "/api/v1/customer/appointments", "/api/v1/customer/memberships", "/api/v1/customer/loyalty/movements" })
    {
      await AssertValidationAsync(context.Client, $"{url}?pageSize=101", "pageSize");
      await AssertValidationAsync(context.Client, $"{url}?page=-1", "page");
    }
  }

  [Fact]
  public async Task EveryPaginatedList_AcceptsAllItsFilters_AndRejectsInvalidValues()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAdminAsync(context, "filters");
    var customerId = await SeedCustomerAsync(context.Factory, "Cliente Filtros", admin.User.Id);
    var staffId = await SeedStaffProfileAsync(context.Factory, "filters");
    var general = ServiceCatalogDefaults.CorteGeneralServiceId;
    var range = $"from={Today.AddDays(-29):yyyy-MM-dd}&to={Today:yyyy-MM-dd}";

    // Every filter at once: the query must translate to SQL and answer a page.
    var urls = new[]
    {
      $"/api/v1/admin/income?{range}&staffProfileId={staffId}&kind=Service&generalServiceId={general}&page=2&pageSize=5",
      $"/api/v1/admin/appointments?{range}&staffProfileId={staffId}&status=Pending,Confirmed&generalServiceId={general}&customer=car&sort=Asc&page=1&pageSize=5",
      $"/api/v1/admin/service-records?status=Approved&{range}&staffProfileId={staffId}&generalServiceId={general}&page=1&pageSize=5",
      $"/api/v1/admin/loyalty/redemptions?status=Expired&customerId={customerId}&{range}&page=1&pageSize=5",
      $"/api/v1/admin/customers?q=fil&page=1&pageSize=5",
      $"/api/v1/admin/services?q=cor&generalServiceId={general}&categoryId={ServiceCatalogDefaults.BarberiaCategoryId}&status=Active&page=1&pageSize=5",
      $"/api/v1/admin/products?q=cera&categoryId={Guid.NewGuid()}&status=Inactive&page=1&pageSize=5",
      $"/api/v1/admin/customers/{customerId}/memberships?status=Active&planId={Guid.NewGuid()}&soldFrom={Today.AddDays(-29):yyyy-MM-dd}&soldTo={Today:yyyy-MM-dd}",
      $"/api/v1/admin/customers/{customerId}/memberships?status=Expired",
      $"/api/v1/admin/customers/{customerId}/memberships?status=Scheduled",
      $"/api/v1/admin/customers/{customerId}/memberships?status=PendingActivation",
      $"/api/v1/admin/customers/{customerId}/loyalty/movements?{range}&type=AdjustmentCredit",
    };

    AuthenticateAs(context.Client, admin.AccessToken);
    foreach (var url in urls)
    {
      using var response = await context.Client.GetAsync(url);
      Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} answered {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    await AssertValidationAsync(context.Client, "/api/v1/admin/income?kind=Gift", "kind");
    await AssertValidationAsync(context.Client, $"/api/v1/admin/income?from={Today:yyyy-MM-dd}&to={Today.AddDays(-1):yyyy-MM-dd}", "from");
    await AssertValidationAsync(context.Client, "/api/v1/admin/appointments?status=Pending,Whatever", "status");
    await AssertValidationAsync(context.Client, "/api/v1/admin/appointments?sort=Sideways", "sort");
    await AssertValidationAsync(context.Client, "/api/v1/admin/services?status=Deleted", "status");
    await AssertValidationAsync(context.Client, "/api/v1/admin/products?status=1", "status");
    await AssertValidationAsync(context.Client, "/api/v1/admin/loyalty/redemptions?status=Lost", "status");
    await AssertValidationAsync(context.Client, $"/api/v1/admin/customers/{customerId}/memberships?status=Paused", "status");
    await AssertValidationAsync(context.Client, $"/api/v1/admin/customers/{customerId}/memberships?soldFrom={Today:yyyy-MM-dd}&soldTo={Today.AddDays(-1):yyyy-MM-dd}", "soldFrom");
    await AssertValidationAsync(context.Client, $"/api/v1/admin/customers/{customerId}/loyalty/movements?type=Gift", "type");

    using var unknownCustomer = await context.Client.GetAsync($"/api/v1/admin/customers/{Guid.NewGuid()}/loyalty/movements");
    Assert.Equal(HttpStatusCode.NotFound, unknownCustomer.StatusCode);

    var customer = await RegisterAsync(context.Client, "Cliente App", UniqueEmail("customer-filters"));
    AuthenticateAs(context.Client, customer.AccessToken);
    await AssertValidationAsync(context.Client, "/api/v1/customer/appointments?scope=Someday", "scope");
    using var own = await context.Client.GetAsync($"/api/v1/customer/memberships?status=Active&soldFrom={Today:yyyy-MM-dd}");
    Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    // Without a linked profile the own lists are an empty page, not an error.
    var movements = await context.Client.GetFromJsonAsync<PagedResult<MyLoyaltyMovementView>>("/api/v1/customer/loyalty/movements?type=Accrual");
    Assert.Equal((0, 1, 25), (movements!.TotalCount, movements.Page, movements.PageSize));
  }

  [Fact]
  public async Task Income_PagesWithEqualDates_WithoutDuplicates_AndTheTotalNeverDependsOnThePage()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAdminAsync(context, "income-pages");
    var alex = await SeedStaffProfileAsync(context.Factory, "alex");
    var bea = await SeedStaffProfileAsync(context.Factory, "bea");
    var color = await SeedGeneralServiceAsync(context.Factory, "Color");
    var cut = await SeedRateAsync(context.Factory, "Corte clásico", ServiceCatalogDefaults.CorteGeneralServiceId);
    var dye = await SeedRateAsync(context.Factory, "Tinte", color);

    // Seven entries of today registered at the very same instant (date and time tie), plus older ones.
    var sameInstant = DateTime.UtcNow;
    await WithDbAsync(context.Factory, async db =>
    {
      for (var index = 0; index < 5; index++)
      {
        db.IncomeEntries.Add(new IncomeEntry(cut.Id, cut.Name, 20000, alex, 1000 + index, false, Today, admin.User.Id, sameInstant));
      }

      db.IncomeEntries.Add(new IncomeEntry(dye.Id, dye.Name, 50000, bea, 50000, false, Today, admin.User.Id, sameInstant));
      db.IncomeEntries.Add(IncomeEntry.CreateMembershipSale(300000, Today, admin.User.Id, sameInstant));
      db.IncomeEntries.Add(new IncomeEntry(cut.Id, cut.Name, 20000, alex, 7000, false, Today.AddDays(-3), admin.User.Id, sameInstant));
      db.IncomeEntries.Add(new IncomeEntry(cut.Id, cut.Name, 20000, bea, 9000, false, Today.AddDays(-40), admin.User.Id, sameInstant));
      await db.SaveChangesAsync();
    });

    AuthenticateAs(context.Client, admin.AccessToken);

    // Default: the last 30 days, today included (the entry of 40 days ago is outside).
    var first = await GetIncomeAsync(context.Client, "pageSize=3");
    Assert.Equal((Today.AddDays(-29), Today, 8, 3), (first.From, first.To, first.TotalCount, first.Items.Count));
    var expectedTotal = 1000 + 1001 + 1002 + 1003 + 1004 + 50000 + 300000 + 7000;
    Assert.Equal(expectedTotal, first.TotalAmount);

    // The count and the total cover the whole filter: they are the same on every page.
    var seen = new List<Guid>();
    for (var page = 1; page <= 3; page++)
    {
      var result = await GetIncomeAsync(context.Client, $"pageSize=3&page={page}");
      Assert.Equal(8, result.TotalCount);
      Assert.Equal(expectedTotal, result.TotalAmount);
      Assert.Equal(page, result.Page);
      seen.AddRange(result.Items.Select(item => item.Id));
    }

    Assert.Equal(8, seen.Count);
    Assert.Equal(8, seen.Distinct().Count());
    Assert.Empty((await GetIncomeAsync(context.Client, "pageSize=3&page=4")).Items);

    // An older period is reachable with its own window of up to 30 days.
    var older = await GetIncomeAsync(context.Client, $"from={Today.AddDays(-45):yyyy-MM-dd}&to={Today.AddDays(-16):yyyy-MM-dd}");
    Assert.Equal((1, 9000), (older.TotalCount, older.TotalAmount));

    // Range (inclusive), professional, kind and general service.
    var range = $"from={Today.AddDays(-7):yyyy-MM-dd}&to={Today:yyyy-MM-dd}";
    var week = await GetIncomeAsync(context.Client, range);
    Assert.Equal(8, week.TotalCount);
    Assert.Equal(Today.AddDays(-3), week.Items[^1].OccurredOn);

    // With only one end the window extends 30 days from it.
    var fromOnly = await GetIncomeAsync(context.Client, $"from={Today.AddDays(-3):yyyy-MM-dd}");
    Assert.Equal((Today.AddDays(-3), Today.AddDays(26), 8), (fromOnly.From, fromOnly.To, fromOnly.TotalCount));
    var toOnly = await GetIncomeAsync(context.Client, $"to={Today.AddDays(-3):yyyy-MM-dd}");
    Assert.Equal((Today.AddDays(-32), Today.AddDays(-3), 1), (toOnly.From, toOnly.To, toOnly.TotalCount));

    var forAlex = await GetIncomeAsync(context.Client, $"{range}&staffProfileId={alex}");
    Assert.Equal(6, forAlex.TotalCount);
    Assert.All(forAlex.Items, item => Assert.Equal(alex, item.StaffProfileId));

    var sales = await GetIncomeAsync(context.Client, $"{range}&kind=MembershipSale");
    Assert.Equal((1, 300000), (sales.TotalCount, sales.TotalAmount));

    var colorIncome = await GetIncomeAsync(context.Client, $"{range}&generalServiceId={color}");
    Assert.Equal((1, 50000), (colorIncome.TotalCount, colorIncome.TotalAmount));
    Assert.Equal(dye.Id, Assert.Single(colorIncome.Items).ServiceId);
  }

  [Fact]
  public async Task AdminAppointments_PagesWithEqualStarts_AndAppliesEveryFilter()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAdminAsync(context, "appointments");
    var alex = await SeedStaffProfileAsync(context.Factory, "appt-alex");
    var bea = await SeedStaffProfileAsync(context.Factory, "appt-bea");
    var color = await SeedGeneralServiceAsync(context.Factory, "Color appt");
    var tomorrowNoon = BogotaClock.ToUtc(Today.AddDays(1), new TimeOnly(12, 0));
    var lastWeek = BogotaClock.ToUtc(Today.AddDays(-7), new TimeOnly(9, 0));

    await WithDbAsync(context.Factory, async db =>
    {
      // Six appointments starting at the same instant (different professionals and customers).
      for (var index = 0; index < 6; index++)
      {
        db.Appointments.Add(Appointment.ForService(
            index % 2 == 0 ? alex : bea,
            index == 0 ? "Carla Gómez" : $"Cliente {index}",
            tomorrowNoon,
            new AppointmentServiceReservation(index == 5 ? color : ServiceCatalogDefaults.CorteGeneralServiceId, "Servicio", 30),
            index == 4 ? AppointmentStatus.Cancelled : AppointmentStatus.Confirmed,
            AppointmentSource.Manual,
            DateTime.UtcNow,
            customerEmail: index == 1 ? "carla.alt@example.com" : null,
            customerPhone: index == 2 ? "3001234567" : null));
      }

      db.Appointments.Add(new Appointment(alex, "Antiguo", lastWeek, lastWeek.AddMinutes(30), AppointmentStatus.Completed, AppointmentSource.Manual, DateTime.UtcNow));
      await db.SaveChangesAsync();
    });

    AuthenticateAs(context.Client, admin.AccessToken);
    // The agenda (from today on, any length) pages the six equal starts without duplicates or gaps.
    var seen = new List<Guid>();
    for (var page = 1; page <= 3; page++)
    {
      var result = await GetPageAsync<AppointmentView>(context.Client, $"/api/v1/admin/appointments?scope=Agenda&sort=Asc&pageSize=2&page={page}");
      Assert.Equal(6, result.TotalCount);
      seen.AddRange(result.Items.Select(item => item.Id));
    }

    Assert.Equal(6, seen.Count);
    Assert.Equal(6, seen.Distinct().Count());

    // The agenda never reaches the past (an earlier "from" is raised to today) and has no 30-day limit.
    Assert.Equal(6, (await GetPageAsync<AppointmentView>(context.Client, $"/api/v1/admin/appointments?scope=Agenda&from={Today.AddDays(-7):yyyy-MM-dd}")).TotalCount);
    Assert.Equal(6, (await GetPageAsync<AppointmentView>(context.Client, $"/api/v1/admin/appointments?scope=Agenda&to={Today.AddDays(90):yyyy-MM-dd}")).TotalCount);

    // The history (the default scope) shows the last 30 days by default: only last week's appointment.
    Assert.Equal("Antiguo", Assert.Single((await GetPageAsync<AppointmentView>(context.Client, "/api/v1/admin/appointments")).Items).CustomerName);

    // Newest first by default; "Asc" puts the oldest first.
    var week = $"from={Today.AddDays(-7):yyyy-MM-dd}&to={Today.AddDays(1):yyyy-MM-dd}";
    Assert.Equal("Antiguo", (await GetPageAsync<AppointmentView>(context.Client, $"/api/v1/admin/appointments?{week}&pageSize=10")).Items[^1].CustomerName);
    Assert.Equal("Antiguo", (await GetPageAsync<AppointmentView>(context.Client, $"/api/v1/admin/appointments?{week}&sort=Asc")).Items[0].CustomerName);

    var tomorrow = $"from={Today.AddDays(1):yyyy-MM-dd}&to={Today.AddDays(1):yyyy-MM-dd}";
    Assert.Equal(6, (await GetPageAsync<AppointmentView>(context.Client, $"/api/v1/admin/appointments?{tomorrow}")).TotalCount);
    Assert.Equal(1, (await GetPageAsync<AppointmentView>(context.Client, $"/api/v1/admin/appointments?to={Today:yyyy-MM-dd}")).TotalCount);
    Assert.Equal(4, (await GetPageAsync<AppointmentView>(context.Client, $"/api/v1/admin/appointments?{week}&staffProfileId={alex}")).TotalCount);
    Assert.Equal(1, (await GetPageAsync<AppointmentView>(context.Client, $"/api/v1/admin/appointments?{week}&status=Cancelled")).TotalCount);
    Assert.Equal(6, (await GetPageAsync<AppointmentView>(context.Client, $"/api/v1/admin/appointments?{week}&status=Confirmed,Completed")).TotalCount);
    Assert.Equal(1, (await GetPageAsync<AppointmentView>(context.Client, $"/api/v1/admin/appointments?{week}&generalServiceId={color}")).TotalCount);

    // The customer filter matches part of the name, the email or the phone, ignoring case.
    var carla = await GetPageAsync<AppointmentView>(context.Client, $"/api/v1/admin/appointments?{week}&customer=CARLA");
    Assert.Equal(2, carla.TotalCount);
    Assert.Equal(1, (await GetPageAsync<AppointmentView>(context.Client, $"/api/v1/admin/appointments?{week}&customer=1234")).TotalCount);

    // An older period: its own 30-day window. 31 days are rejected; an unknown scope too.
    Assert.Equal(1, (await GetPageAsync<AppointmentView>(context.Client, $"/api/v1/admin/appointments?scope=History&from={Today.AddDays(-36):yyyy-MM-dd}&to={Today.AddDays(-7):yyyy-MM-dd}")).TotalCount);
    await AssertValidationAsync(context.Client, $"/api/v1/admin/appointments?scope=History&from={Today.AddDays(-30):yyyy-MM-dd}&to={Today:yyyy-MM-dd}", "to");
    await AssertValidationAsync(context.Client, "/api/v1/admin/appointments?scope=Forever", "scope");

    using var unknownStaff = await context.Client.GetAsync($"/api/v1/admin/appointments?staffProfileId={Guid.NewGuid()}");
    Assert.Equal(HttpStatusCode.NotFound, unknownStaff.StatusCode);
  }

  [Fact]
  public async Task StaffAndCustomerAppointments_FilterTheirOwnAgenda()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var staff = await RegisterStaffAsync(context, "own");
    var customer = await RegisterAsync(context.Client, "Cliente Propio", UniqueEmail("customer-own"));
    var other = await RegisterAsync(context.Client, "Otro Cliente", UniqueEmail("customer-other"));
    var staffProfileId = await WithDbAsync(context.Factory, db => db.StaffProfiles.Where(p => p.UserId == staff.User.Id).Select(p => p.Id).SingleAsync());
    var future = BogotaClock.ToUtc(Today.AddDays(2), new TimeOnly(10, 0));
    var past = BogotaClock.ToUtc(Today.AddDays(-2), new TimeOnly(10, 0));

    await WithDbAsync(context.Factory, async db =>
    {
      Appointment Add(DateTime startsAt, AppointmentStatus status, Guid? customerUserId)
      {
        var appointment = new Appointment(staffProfileId, "Cliente", startsAt, startsAt.AddMinutes(30), status, AppointmentSource.Manual, DateTime.UtcNow, customerUserId);
        db.Appointments.Add(appointment);
        return appointment;
      }

      Add(future, AppointmentStatus.Pending, customer.User.Id);
      Add(future.AddHours(1), AppointmentStatus.Confirmed, customer.User.Id);
      Add(future.AddHours(2), AppointmentStatus.Cancelled, customer.User.Id);
      Add(past, AppointmentStatus.Completed, customer.User.Id);
      Add(past.AddHours(1), AppointmentStatus.NoShow, customer.User.Id);
      Add(past, AppointmentStatus.Completed, other.User.Id);
      await db.SaveChangesAsync();
    });

    AuthenticateAs(context.Client, staff.AccessToken);
    // History by default (the last 30 days): the three past ones; the agenda: the three from today on.
    Assert.Equal(3, (await GetPageAsync<AppointmentView>(context.Client, "/api/v1/staff/appointments")).TotalCount);
    Assert.Equal(3, (await GetPageAsync<AppointmentView>(context.Client, "/api/v1/staff/appointments?scope=Agenda")).TotalCount);
    await AssertValidationAsync(context.Client, $"/api/v1/staff/appointments?scope=History&from={Today.AddDays(-30):yyyy-MM-dd}&to={Today:yyyy-MM-dd}", "to");
    var staffFuture = await GetPageAsync<AppointmentView>(context.Client, $"/api/v1/staff/appointments?scope=Agenda&status=Pending,Confirmed&sort=Asc");
    Assert.Equal(2, staffFuture.TotalCount);
    Assert.True(staffFuture.Items[0].StartsAtUtc < staffFuture.Items[1].StartsAtUtc);
    Assert.Equal(2, (await GetPageAsync<AppointmentView>(context.Client, $"/api/v1/staff/appointments?to={Today.AddDays(-1):yyyy-MM-dd}&status=Completed")).TotalCount);

    AuthenticateAs(context.Client, customer.AccessToken);
    // Without a scope the list is a history: the last 30 days by default; any other window on request.
    Assert.Equal(2, (await GetPageAsync<AppointmentView>(context.Client, "/api/v1/customer/appointments")).TotalCount);
    Assert.Equal(5, (await GetPageAsync<AppointmentView>(context.Client, $"/api/v1/customer/appointments?from={Today.AddDays(-10):yyyy-MM-dd}")).TotalCount);

    // Upcoming = what can still be attended, soonest first; past = everything else (the cancelled future one too).
    var upcoming = await GetPageAsync<AppointmentView>(context.Client, "/api/v1/customer/appointments?scope=Upcoming");
    Assert.Equal([AppointmentStatus.Pending, AppointmentStatus.Confirmed], upcoming.Items.Select(item => item.Status));
    var pastPage = await GetPageAsync<AppointmentView>(context.Client, $"/api/v1/customer/appointments?scope=past&from={Today.AddDays(-10):yyyy-MM-dd}&pageSize=2");
    Assert.Equal((3, 2), (pastPage.TotalCount, pastPage.Items.Count));
    Assert.Equal(AppointmentStatus.Cancelled, pastPage.Items[0].Status);
    // Upcoming is a queue: no window. Past is a history: 30 days at most.
    await AssertValidationAsync(context.Client, $"/api/v1/customer/appointments?scope=Past&from={Today.AddDays(-30):yyyy-MM-dd}&to={Today:yyyy-MM-dd}", "to");
    Assert.Equal(1, (await GetPageAsync<AppointmentView>(context.Client, "/api/v1/customer/appointments?scope=Past&status=NoShow")).TotalCount);
    Assert.Equal(2, (await GetPageAsync<AppointmentView>(context.Client, $"/api/v1/customer/appointments?scope=Past&to={Today:yyyy-MM-dd}")).TotalCount);
  }

  [Fact]
  public async Task CustomersRatesAndProducts_PageAndFilterByTextCategoryAndStatus()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAdminAsync(context, "catalog");
    for (var index = 0; index < 5; index++)
    {
      // Equal names: the id breaks the tie.
      await SeedCustomerAsync(context.Factory, "Mismo Nombre", admin.User.Id, $"30012345{index:00}");
    }

    await SeedCustomerAsync(context.Factory, "Zoe Final", admin.User.Id, "3109990000");

    var color = await SeedGeneralServiceAsync(context.Factory, "Color catálogo");
    await SeedRateAsync(context.Factory, "Corte niño", ServiceCatalogDefaults.CorteGeneralServiceId);
    await SeedRateAsync(context.Factory, "Corte adulto", ServiceCatalogDefaults.CorteGeneralServiceId, active: false);
    await SeedRateAsync(context.Factory, "Tinte completo", color);
    var (waxes, oils) = await SeedProductsAsync(context.Factory);

    AuthenticateAs(context.Client, admin.AccessToken);
    var seen = new List<Guid>();
    for (var page = 1; page <= 3; page++)
    {
      var result = await GetPageAsync<CustomerListItem>(context.Client, $"/api/v1/admin/customers?q=mismo&pageSize=2&page={page}");
      Assert.Equal(5, result.TotalCount);
      seen.AddRange(result.Items.Select(item => item.Id));
    }

    Assert.Equal(5, seen.Distinct().Count());
    Assert.Equal(6, (await GetPageAsync<CustomerListItem>(context.Client, "/api/v1/admin/customers")).TotalCount);
    Assert.Equal("Zoe Final", Assert.Single((await GetPageAsync<CustomerListItem>(context.Client, "/api/v1/admin/customers?q=3109990000")).Items).FullName);

    Assert.Equal(2, (await GetPageAsync<ServiceView>(context.Client, "/api/v1/admin/services?q=CORTE")).TotalCount);
    Assert.Equal("Corte adulto", Assert.Single((await GetPageAsync<ServiceView>(context.Client, "/api/v1/admin/services?q=corte&status=Inactive")).Items).Name);
    Assert.Equal("Tinte completo", Assert.Single((await GetPageAsync<ServiceView>(context.Client, $"/api/v1/admin/services?generalServiceId={color}")).Items).Name);
    Assert.Equal(3, (await GetPageAsync<ServiceView>(context.Client, $"/api/v1/admin/services?categoryId={ServiceCatalogDefaults.BarberiaCategoryId}")).TotalCount);
    var options = await context.Client.GetFromJsonAsync<List<ServiceView>>("/api/v1/admin/services/options");
    Assert.Equal(3, options!.Count);

    var products = await GetPageAsync<ProductView>(context.Client, "/api/v1/admin/products?pageSize=2");
    Assert.Equal((4, 2), (products.TotalCount, products.Items.Count));
    // Catalog order: the category order first (waxes, then oils).
    Assert.All(products.Items, product => Assert.Equal(waxes, product.CategoryId));
    Assert.Equal(1, (await GetPageAsync<ProductView>(context.Client, $"/api/v1/admin/products?categoryId={oils}")).TotalCount);
    Assert.Equal("Cera mate", Assert.Single((await GetPageAsync<ProductView>(context.Client, "/api/v1/admin/products?q=MATE")).Items).Name);
    Assert.Equal("Pomada", Assert.Single((await GetPageAsync<ProductView>(context.Client, "/api/v1/admin/products?status=Inactive")).Items).Name);
  }

  [Fact]
  public async Task LoyaltyMovements_ArePaginatedForTheAdminAndTheCustomer_ByTypeAndRange()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAdminAsync(context, "movements");
    var customer = await RegisterAsync(context.Client, "Cliente Puntos", UniqueEmail("customer-points"));
    var customerId = await SeedCustomerAsync(context.Factory, "Cliente Puntos", admin.User.Id, linkedUserId: customer.User.Id);

    using (var scope = context.Factory.Services.CreateScope())
    {
      var wallet = scope.ServiceProvider.GetRequiredService<IAdminCustomerLoyaltyService>();
      for (var index = 0; index < 12; index++)
      {
        await wallet.AdjustAsync(admin.User.Id, customerId, new LoyaltyAdjustmentRequest("Credit", 10 + index, "Bono de prueba", Guid.NewGuid()));
      }

      await wallet.AdjustAsync(admin.User.Id, customerId, new LoyaltyAdjustmentRequest("Debit", 5, "Corrección", Guid.NewGuid()));
    }

    AuthenticateAs(context.Client, admin.AccessToken);
    var walletView = await context.Client.GetFromJsonAsync<LoyaltyWalletView>($"/api/v1/admin/customers/{customerId}/loyalty");
    // The wallet only embeds the latest movements; the history is the paginated list.
    Assert.Equal(LoyaltyMovementListQuery.RecentPreviewSize, walletView!.Movements.Count);

    var url = $"/api/v1/admin/customers/{customerId}/loyalty/movements";
    var seen = new List<Guid>();
    for (var page = 1; page <= 3; page++)
    {
      var result = await GetPageAsync<LoyaltyMovementView>(context.Client, $"{url}?pageSize=5&page={page}");
      Assert.Equal(13, result.TotalCount);
      seen.AddRange(result.Items.Select(item => item.Id));
    }

    Assert.Equal(13, seen.Distinct().Count());
    Assert.Equal("AdjustmentDebit", (await GetPageAsync<LoyaltyMovementView>(context.Client, url)).Items[0].Type);
    Assert.Equal(1, (await GetPageAsync<LoyaltyMovementView>(context.Client, $"{url}?type=AdjustmentDebit")).TotalCount);
    Assert.Equal(13, (await GetPageAsync<LoyaltyMovementView>(context.Client, $"{url}?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}")).TotalCount);
    Assert.Equal(0, (await GetPageAsync<LoyaltyMovementView>(context.Client, $"{url}?from={Today.AddDays(1):yyyy-MM-dd}")).TotalCount);

    AuthenticateAs(context.Client, customer.AccessToken);
    var mine = await context.Client.GetFromJsonAsync<MyLoyaltyView>("/api/v1/customer/loyalty");
    Assert.Equal(LoyaltyMovementListQuery.RecentPreviewSize, mine!.Movements.Count);
    var ownPage = await GetPageAsync<MyLoyaltyMovementView>(context.Client, "/api/v1/customer/loyalty/movements?pageSize=10&page=2");
    Assert.Equal((13, 3), (ownPage.TotalCount, ownPage.Items.Count));
    Assert.Equal(12, (await GetPageAsync<MyLoyaltyMovementView>(context.Client, "/api/v1/customer/loyalty/movements?type=AdjustmentCredit")).TotalCount);
  }

  [Fact]
  public async Task Histories_AcceptThirtyDays_RejectThirtyOne_OnTheServer_AndDefaultToTheLastThirty()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAdminAsync(context, "windows");
    var customerId = await SeedCustomerAsync(context.Factory, "Cliente Ventanas", admin.User.Id);
    var thirty = $"from={Today.AddDays(-29):yyyy-MM-dd}&to={Today:yyyy-MM-dd}";
    var thirtyOne = $"from={Today.AddDays(-30):yyyy-MM-dd}&to={Today:yyyy-MM-dd}";
    var oldThirty = $"from={Today.AddDays(-400):yyyy-MM-dd}&to={Today.AddDays(-371):yyyy-MM-dd}";

    var histories = new[]
    {
      "/api/v1/admin/income?x=1",
      "/api/v1/admin/appointments?scope=History",
      "/api/v1/admin/service-records?status=Approved",
      "/api/v1/admin/service-records?status=Rejected",
      "/api/v1/admin/loyalty/redemptions?status=Delivered",
      "/api/v1/admin/loyalty/redemptions?x=1",
      $"/api/v1/admin/customers/{customerId}/loyalty/movements?x=1",
    };

    AuthenticateAs(context.Client, admin.AccessToken);
    foreach (var url in histories)
    {
      using var accepted = await context.Client.GetAsync($"{url}&{thirty}");
      Assert.True(accepted.StatusCode == HttpStatusCode.OK, $"{url} rejected 30 days: {accepted.StatusCode}");
      using var old = await context.Client.GetAsync($"{url}&{oldThirty}");
      Assert.True(old.StatusCode == HttpStatusCode.OK, $"{url} rejected an old period: {old.StatusCode}");
      await AssertValidationAsync(context.Client, $"{url}&{thirtyOne}", "to");
    }

    // Queues and current items take no default date, but a range given to them is limited too.
    foreach (var url in new[]
             {
               "/api/v1/admin/service-records?status=Pending",
               "/api/v1/admin/loyalty/redemptions?status=Requested",
               "/api/v1/admin/appointments?scope=Agenda",
             })
    {
      using var withoutDates = await context.Client.GetAsync(url);
      Assert.Equal(HttpStatusCode.OK, withoutDates.StatusCode);
    }

    await AssertValidationAsync(context.Client, $"/api/v1/admin/service-records?status=Pending&{thirtyOne}", "to");
    await AssertValidationAsync(context.Client, $"/api/v1/admin/loyalty/redemptions?status=Approved&{thirtyOne}", "to");
    await AssertValidationAsync(
        context.Client,
        $"/api/v1/admin/customers/{customerId}/memberships?soldFrom={Today.AddDays(-30):yyyy-MM-dd}&soldTo={Today:yyyy-MM-dd}",
        "soldTo");

    // The default window that income reports back.
    var income = await GetIncomeAsync(context.Client, string.Empty);
    Assert.Equal((Today.AddDays(-29), Today), (income.From, income.To));

    var customer = await RegisterAsync(context.Client, "Cliente Ventanas App", UniqueEmail("windows"));
    AuthenticateAs(context.Client, customer.AccessToken);
    foreach (var url in new[] { "/api/v1/customer/appointments?scope=Past", "/api/v1/customer/loyalty/movements?x=1" })
    {
      using var accepted = await context.Client.GetAsync($"{url}&{thirty}");
      Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
      await AssertValidationAsync(context.Client, $"{url}&{thirtyOne}", "to");
    }

    await AssertValidationAsync(
        context.Client, $"/api/v1/customer/memberships?soldFrom={Today.AddDays(-30):yyyy-MM-dd}&soldTo={Today:yyyy-MM-dd}", "soldTo");
  }

  [Fact]
  public async Task OldPendingItems_StayVisibleInTheirQueues_WhileHistoriesDefaultToTheLastThirtyDays()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAdminAsync(context, "old-pending");
    var staffId = await SeedStaffProfileAsync(context.Factory, "old-pending");
    var customerId = await SeedCustomerAsync(context.Factory, "Cliente Antiguo", admin.User.Id);
    var claimant = await RegisterAsync(context.Client, "Reclamante Antiguo", UniqueEmail("claimant"));
    var ninetyDaysAgo = DateTime.UtcNow.AddDays(-90);
    var oldDay = Today.AddDays(-90);

    var ids = await WithDbAsync(context.Factory, async db =>
    {
      var pendingRecord = OldRecord(staffId, admin.User.Id, oldDay, ninetyDaysAgo);
      var rejectedRecord = OldRecord(staffId, admin.User.Id, oldDay, ninetyDaysAgo);
      rejectedRecord.Reject("Duplicado", admin.User.Id, ninetyDaysAgo.AddHours(1));
      db.ServiceRecords.AddRange(pendingRecord, rejectedRecord);

      var reward = new LoyaltyReward("Gorra", null, LoyaltyRewardKind.Other, null, 10, admin.User.Id, ninetyDaysAgo);
      db.LoyaltyRewards.Add(reward);
      var requested = OldRedemption(customerId, reward, admin.User.Id, ninetyDaysAgo, oldDay);
      var approved = OldRedemption(customerId, reward, admin.User.Id, ninetyDaysAgo, oldDay);
      approved.Approve(admin.User.Id, ninetyDaysAgo.AddDays(1), oldDay.AddDays(1));
      db.LoyaltyRedemptions.AddRange(requested, approved);

      var claim = new CustomerClaim(claimant.User.Id, Guid.NewGuid(), "Reclamante Antiguo", "3001234567", claimant.User.Email, null, ninetyDaysAgo);
      db.CustomerClaims.Add(claim);
      await db.SaveChangesAsync();
      return (PendingRecord: pendingRecord.Id, RejectedRecord: rejectedRecord.Id, Requested: requested.Id, Approved: approved.Id, Claim: claim.Id);
    });

    AuthenticateAs(context.Client, admin.AccessToken);

    // Queues: no default date, so the 90-day-old pending items are listed.
    Assert.Contains(ids.PendingRecord, (await GetPageAsync<ServiceRecordView>(context.Client, "/api/v1/admin/service-records")).Items.Select(item => item.Id));
    Assert.Contains(ids.Requested, (await GetPageAsync<LoyaltyRedemptionView>(context.Client, "/api/v1/admin/loyalty/redemptions?status=Requested")).Items.Select(item => item.Id));
    Assert.Contains(ids.Approved, (await GetPageAsync<LoyaltyRedemptionView>(context.Client, "/api/v1/admin/loyalty/redemptions?status=Approved")).Items.Select(item => item.Id));
    Assert.Contains(ids.Approved, (await GetPageAsync<LoyaltyRedemptionView>(context.Client, "/api/v1/admin/loyalty/redemptions/deliveries")).Items.Select(item => item.Id));
    Assert.Contains(ids.Claim, (await GetPageAsync<CustomerClaimView>(context.Client, "/api/v1/admin/customer-claims")).Items.Select(item => item.Id));

    // Histories: the last 30 days by default, so the old items are only in their own window.
    Assert.Equal(0, (await GetPageAsync<ServiceRecordView>(context.Client, "/api/v1/admin/service-records?status=Rejected")).TotalCount);
    Assert.Equal(0, (await GetPageAsync<LoyaltyRedemptionView>(context.Client, "/api/v1/admin/loyalty/redemptions")).TotalCount);
    var oldWindow = $"from={Today.AddDays(-100):yyyy-MM-dd}&to={Today.AddDays(-71):yyyy-MM-dd}";
    Assert.Equal(ids.RejectedRecord, Assert.Single((await GetPageAsync<ServiceRecordView>(context.Client, $"/api/v1/admin/service-records?status=Rejected&{oldWindow}")).Items).Id);
    Assert.Equal(2, (await GetPageAsync<LoyaltyRedemptionView>(context.Client, $"/api/v1/admin/loyalty/redemptions?{oldWindow}")).TotalCount);

    // The wallet summary keeps every pending redemption, however old.
    var wallet = await context.Client.GetFromJsonAsync<LoyaltyWalletView>($"/api/v1/admin/customers/{customerId}/loyalty");
    Assert.Equal(new[] { ids.Requested, ids.Approved }.Order(), wallet!.Redemptions.Select(item => item.Id).Order());
  }

  [Fact]
  public async Task ListsThatHadFixedCaps_ReachEveryRowPastTheOldCap_PendingFirst()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAdminAsync(context, "caps");
    var staff = await RegisterStaffAsync(context, "caps");
    var customer = await RegisterAsync(context.Client, "Cliente Topes", UniqueEmail("caps"));
    var customerId = await SeedCustomerAsync(context.Factory, "Cliente Topes", admin.User.Id, linkedUserId: customer.User.Id);
    var now = DateTime.UtcNow;

    var oldestPending = await WithDbAsync(context.Factory, async db =>
    {
      var staffProfileId = await db.StaffProfiles.Where(profile => profile.UserId == staff.User.Id).Select(profile => profile.Id).SingleAsync();

      // Own records: 105 (the old cap was 100); the oldest one is still pending.
      var pending = OldRecord(staffProfileId, staff.User.Id, Today.AddDays(-200), now.AddDays(-200));
      db.ServiceRecords.Add(pending);
      for (var index = 0; index < 104; index++)
      {
        var record = OldRecord(staffProfileId, staff.User.Id, Today, now.AddMinutes(-index));
        record.Reject("Revisado", admin.User.Id, now);
        db.ServiceRecords.Add(record);
      }

      // 201 approved redemptions of one customer (deliveries and "Mis canjes" were capped at 200).
      var reward = new LoyaltyReward("Gorra", null, LoyaltyRewardKind.Other, null, 1, admin.User.Id, now);
      db.LoyaltyRewards.Add(reward);
      for (var index = 0; index < 201; index++)
      {
        var redemption = OldRedemption(customerId, reward, customer.User.Id, now.AddMinutes(-index), Today);
        redemption.Approve(admin.User.Id, now, Today);
        db.LoyaltyRedemptions.Add(redemption);
      }

      // 201 rejected claims (claims were capped at 200).
      for (var index = 0; index < 201; index++)
      {
        var claim = new CustomerClaim(customer.User.Id, Guid.NewGuid(), "Cliente Topes", "3001234567", customer.User.Email, null, now.AddMinutes(-index));
        claim.Reject("No coincide", admin.User.Id, now.AddMinutes(-index));
        db.CustomerClaims.Add(claim);
      }

      await db.SaveChangesAsync();
      return pending.Id;
    });

    async Task<List<Guid>> ReadAllAsync<T>(string url, int expectedTotal, Func<T, Guid> id)
    {
      var ids = new List<Guid>();
      var pages = (expectedTotal + PageRequest.DefaultPageSize - 1) / PageRequest.DefaultPageSize;
      for (var page = 1; page <= pages; page++)
      {
        var result = await GetPageAsync<T>(context.Client, $"{url}{(url.Contains('?') ? '&' : '?')}page={page}");
        Assert.Equal(expectedTotal, result.TotalCount);
        ids.AddRange(result.Items.Select(id));
      }

      Assert.Equal(expectedTotal, ids.Distinct().Count());
      return ids;
    }

    AuthenticateAs(context.Client, staff.AccessToken);
    var ownRecords = await ReadAllAsync<ServiceRecordView>("/api/v1/staff/service-records", 105, item => item.Id);
    Assert.Equal(oldestPending, ownRecords[0]);

    AuthenticateAs(context.Client, admin.AccessToken);
    await ReadAllAsync<LoyaltyRedemptionView>("/api/v1/admin/loyalty/redemptions/deliveries", 201, item => item.Id);
    await ReadAllAsync<CustomerClaimView>("/api/v1/admin/customer-claims?status=Rejected", 201, item => item.Id);

    AuthenticateAs(context.Client, customer.AccessToken);
    await ReadAllAsync<MyLoyaltyRedemptionView>("/api/v1/customer/loyalty/redemptions", 201, item => item.Id);
  }

  [Fact]
  public async Task TheWalletBalance_DoesNotDependOnTheMovementsPage()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAdminAsync(context, "balances");
    var customerId = await SeedCustomerAsync(context.Factory, "Cliente Saldos", admin.User.Id);
    await WithDbAsync(context.Factory, async db =>
    {
      var settings = await db.LoyaltySettings.SingleAsync();
      settings.Update(true, 365, admin.User.Id, DateTime.UtcNow);
      await db.SaveChangesAsync();
    });

    AuthenticateAs(context.Client, admin.AccessToken);
    foreach (var points in new[] { 10, 20, 30 })
    {
      using var credited = await context.Client.PostAsJsonAsync(
          $"/api/v1/admin/customers/{customerId}/loyalty/adjustments",
          new LoyaltyAdjustmentRequest("Credit", points, "Puntos de prueba", Guid.NewGuid()));
      Assert.Equal(HttpStatusCode.OK, credited.StatusCode);
    }

    var walletUrl = $"/api/v1/admin/customers/{customerId}/loyalty";
    var balance = (await context.Client.GetFromJsonAsync<LoyaltyWalletView>(walletUrl))!.AvailablePoints;
    Assert.Equal(60, balance);

    var seen = new List<int>();
    for (var page = 1; page <= 3; page++)
    {
      var movements = await GetPageAsync<LoyaltyMovementView>(context.Client, $"{walletUrl}/movements?pageSize=1&page={page}");
      Assert.Equal(3, movements.TotalCount);
      seen.Add(Assert.Single(movements.Items).Points);
      Assert.Equal(balance, (await context.Client.GetFromJsonAsync<LoyaltyWalletView>(walletUrl))!.AvailablePoints);
    }

    Assert.Equal(60, seen.Sum());
  }

  private static ServiceRecord OldRecord(Guid staffProfileId, Guid registeredBy, DateOnly performedOn, DateTime registeredAt)
      => new(Guid.NewGuid(), 0, null, staffProfileId, null, "Corte", performedOn, null, "Cliente", null, registeredBy, registeredAt,
          ServiceCatalogDefaults.CorteGeneralServiceId);

  private static LoyaltyRedemption OldRedemption(Guid customerId, LoyaltyReward reward, Guid requestedBy, DateTime requestedAt, DateOnly requestedOn)
      => new(customerId, reward, null, LoyaltyRedemptionChannel.App, false, Guid.NewGuid(), requestedBy, requestedAt, requestedOn, 30, 30);

  // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

  private static async Task AssertValidationAsync(HttpClient client, string url, string field)
  {
    using var response = await client.GetAsync(url);
    Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{url} answered {response.StatusCode}.");
    var body = await response.Content.ReadAsStringAsync();
    Assert.Contains($"\"{field}\"", body, StringComparison.Ordinal);
  }

  private static async Task<IncomeEntriesPage> GetIncomeAsync(HttpClient client, string query)
  {
    var result = await client.GetFromJsonAsync<IncomeEntriesPage>($"/api/v1/admin/income?{query}");
    Assert.NotNull(result);
    return result!;
  }

  private static async Task<PagedResult<T>> GetPageAsync<T>(HttpClient client, string url)
  {
    using var response = await client.GetAsync(url);
    Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} answered {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    var result = await response.Content.ReadFromJsonAsync<PagedResult<T>>(Json);
    Assert.NotNull(result);
    return result!;
  }

  private static async Task WithDbAsync(IntegrationTestFactory factory, Func<AppDbContext, Task> action)
  {
    using var scope = factory.Services.CreateScope();
    await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
  }

  private static async Task<T> WithDbAsync<T>(IntegrationTestFactory factory, Func<AppDbContext, Task<T>> action)
  {
    using var scope = factory.Services.CreateScope();
    return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
  }

  private static Task<Guid> SeedCustomerAsync(
      IntegrationTestFactory factory, string name, Guid createdBy, string? phone = null, Guid? linkedUserId = null)
      => WithDbAsync(factory, async db =>
      {
        var customer = new CustomerProfile(name, phone ?? $"300{Random.Shared.Next(1000000, 9999999)}", UniqueEmail("profile"), createdBy, DateTime.UtcNow);
        if (linkedUserId is { } userId)
        {
          customer.LinkAccount(userId, createdBy, DateTime.UtcNow);
        }

        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
      });

  private static Task<Guid> SeedStaffProfileAsync(IntegrationTestFactory factory, string label)
      => WithDbAsync(factory, async db =>
      {
        var user = new User($"Barbero {label}", UniqueEmail($"staff-{label}"), "hash", DateTime.UtcNow);
        db.Users.Add(user);
        var profile = new StaffProfile(user.Id, $"Barbero {label}", 30, DateTime.UtcNow);
        db.StaffProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
      });

  private static Task<Guid> SeedGeneralServiceAsync(IntegrationTestFactory factory, string name)
      => WithDbAsync(factory, async db =>
      {
        var general = new GeneralService(ServiceCatalogDefaults.BarberiaCategoryId, name, null, 1, DateTime.UtcNow);
        db.GeneralServices.Add(general);
        await db.SaveChangesAsync();
        return general.Id;
      });

  private static Task<Service> SeedRateAsync(IntegrationTestFactory factory, string name, Guid generalServiceId, bool active = true)
      => WithDbAsync(factory, async db =>
      {
        var rate = new Service(name, 25000, DateTime.UtcNow, 40, generalServiceId);
        if (!active)
        {
          rate.Deactivate(DateTime.UtcNow);
        }

        db.Services.Add(rate);
        await db.SaveChangesAsync();
        return rate;
      });

  private static Task<(Guid Waxes, Guid Oils)> SeedProductsAsync(IntegrationTestFactory factory)
      => WithDbAsync(factory, async db =>
      {
        var waxes = new ProductCategory("Ceras", 0, DateTime.UtcNow);
        var oils = new ProductCategory("Aceites", 1, DateTime.UtcNow);
        db.ProductCategories.AddRange(waxes, oils);
        var inactive = new Product(waxes.Id, "Pomada", null, 30000, 2, DateTime.UtcNow);
        inactive.Deactivate(DateTime.UtcNow);
        db.Products.AddRange(
            new Product(waxes.Id, "Cera mate", null, 35000, 0, DateTime.UtcNow),
            new Product(waxes.Id, "Cera brillante", null, 35000, 1, DateTime.UtcNow),
            inactive,
            new Product(oils.Id, "Aceite de barba", null, 20000, 0, DateTime.UtcNow));
        await db.SaveChangesAsync();
        return (waxes.Id, oils.Id);
      });

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

  private static async Task<AuthPayload> RegisterStaffAsync(TestHttpContext context, string label)
  {
    var email = UniqueEmail($"staff-{label}");
    await RegisterAsync(context.Client, $"Barbero {label}", email);
    await WithDbAsync(context.Factory, async db =>
    {
      var user = await db.Users.Include(candidate => candidate.UserRoles).SingleAsync(candidate => candidate.NormalizedEmail == email.ToUpperInvariant());
      var staffRole = await db.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Staff.ToUpperInvariant());
      user.UserRoles.Add(new UserRole(user.Id, staffRole.Id, DateTime.UtcNow));
      db.StaffProfiles.Add(new StaffProfile(user.Id, $"Barbero {label}", 30, DateTime.UtcNow));
      await db.SaveChangesAsync();
    });

    return await LoginAsync(context.Client, email);
  }

  private static async Task<AuthPayload> RegisterAdminAsync(TestHttpContext context, string label)
  {
    var email = UniqueEmail($"admin-{label}");
    await RegisterAsync(context.Client, $"Admin {label}", email);
    await WithDbAsync(context.Factory, async db =>
    {
      var user = await db.Users.Include(candidate => candidate.UserRoles).SingleAsync(candidate => candidate.NormalizedEmail == email.ToUpperInvariant());
      var adminRole = await db.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Admin.ToUpperInvariant());
      user.UserRoles.Add(new UserRole(user.Id, adminRole.Id, DateTime.UtcNow));
      await db.SaveChangesAsync();
    });

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
