using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Staff.Admin;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Api;

/// <summary>
/// SONDA lote C2 over real HTTP: NUL characters (BUG-039), technical .NET messages and extreme dates (BUG-021, BUG-040,
/// BUG-006), omitted value properties (BUG-069, BUG-081), concurrent writes (BUG-072), a null business day (BUG-097)
/// and the correlation header on errors (BUG-089).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "ApiInputErrorsHttpIntegration")]
public sealed class ApiInputErrorsHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";
  private static readonly DateTime Tomorrow = DateTime.UtcNow.Date.AddDays(1).AddHours(15);

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public ApiInputErrorsHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  // ── BUG-039 ──

  [Fact]
  public async Task ANulCharacter_Answers422WithTheField_InBodiesQueriesAndAnonymousRoutes()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    using (var anonymous = await context.Client.GetAsync("/api/v1/public/staff?search=%00"))
    {
      var problem = await AssertValidationAsync(anonymous, HttpStatusCode.UnprocessableEntity);
      Assert.Equal("El texto contiene caracteres no permitidos.", problem.GetProperty("errors").GetProperty("search")[0].GetString());
    }

    var customer = await RegisterAsync(context.Client, "nul-customer");
    Authenticate(context.Client, customer);
    using (var booking = await PostJsonAsync(context.Client, "/api/v1/customer/appointments",
               $$"""{"staffProfileId":"{{Guid.NewGuid()}}","generalServiceId":"{{Guid.NewGuid()}}","startsAtUtc":"{{Tomorrow:O}}","notes":"nul\u0000x"}"""))
    {
      var problem = await AssertValidationAsync(booking, HttpStatusCode.UnprocessableEntity);
      Assert.True(problem.GetProperty("errors").TryGetProperty("notes", out _));
    }

    using (var profile = await context.Client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, "/api/v1/customer/profile")
           {
             Content = new StringContent("""{"fullName":"QA\u0000_TEST","phoneNumber":null,"dateOfBirth":null}""", Encoding.UTF8, "application/json")
           }))
    {
      var problem = await AssertValidationAsync(profile, HttpStatusCode.UnprocessableEntity);
      Assert.True(problem.GetProperty("errors").TryGetProperty("fullName", out _));
    }

    var admin = await RegisterAdminAsync(context, "nul-admin");
    Authenticate(context.Client, admin);
    using (var banner = await PostJsonAsync(context.Client, "/api/v1/admin/content/banners",
               """{"title":"Promo\u0000","subtitle":null,"imageMediaAssetId":null,"linkUrl":null,"sortOrder":1,"isActive":false,"startsAtUtc":null,"endsAtUtc":null}"""))
    {
      var problem = await AssertValidationAsync(banner, HttpStatusCode.UnprocessableEntity);
      Assert.True(problem.GetProperty("errors").TryGetProperty("title", out _));
    }

    using (var search = await context.Client.GetAsync("/api/v1/admin/appointments?customer=QA%00x"))
    {
      await AssertValidationAsync(search, HttpStatusCode.UnprocessableEntity);
    }
  }

  // ── BUG-040 / BUG-021 / BUG-006 ──

  [Fact]
  public async Task ExtremeDates_Answer422InSpanish_WithoutTheDotNetMessage()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    using (var slots = await context.Client.GetAsync(
               $"/api/v1/public/staff/{Guid.NewGuid()}/availability/slots?from=9999-12-31&to=9999-12-31&generalServiceId={Guid.NewGuid()}"))
    {
      var problem = await AssertValidationAsync(slots, HttpStatusCode.UnprocessableEntity);
      Assert.Equal("La fecha está fuera del rango permitido (entre 1900 y 2100).", problem.GetProperty("errors").GetProperty("from")[0].GetString());
    }

    var customer = await RegisterAsync(context.Client, "dates-customer");
    Authenticate(context.Client, customer);
    using var booking = await PostJsonAsync(context.Client, "/api/v1/customer/appointments",
        $$"""{"staffProfileId":"{{Guid.NewGuid()}}","generalServiceId":"{{Guid.NewGuid()}}","startsAtUtc":"9999-12-31T23:45:00Z","notes":null}""");
    var bookingProblem = await AssertValidationAsync(booking, HttpStatusCode.UnprocessableEntity);
    Assert.True(bookingProblem.GetProperty("errors").TryGetProperty("startsAtUtc", out _));
    Assert.DoesNotContain("un-representable", bookingProblem.GetRawText());
  }

  [Fact]
  public async Task TooLongAppointmentDetails_Answer422PerFieldInSpanish()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var customer = await RegisterAsync(context.Client, "long-customer");
    Authenticate(context.Client, customer);
    using (var booking = await context.Client.PostAsJsonAsync("/api/v1/customer/appointments", new
           {
             staffProfileId = Guid.NewGuid(), generalServiceId = Guid.NewGuid(), startsAtUtc = Tomorrow, notes = new string('n', 2001)
           }))
    {
      var problem = await AssertValidationAsync(booking, HttpStatusCode.UnprocessableEntity);
      Assert.Equal("Las notas no pueden superar 2000 caracteres.", problem.GetProperty("errors").GetProperty("notes")[0].GetString());
    }

    var admin = await RegisterAdminAsync(context, "long-admin");
    Authenticate(context.Client, admin);
    using var manual = await context.Client.PostAsJsonAsync("/api/v1/admin/appointments", new
    {
      staffProfileId = Guid.NewGuid(), startsAtUtc = Tomorrow, generalServiceId = Guid.NewGuid(),
      customerName = "A", customerEmail = (string?)null, customerPhone = new string('3', 41), notes = new string('n', 2001)
    });
    var manualProblem = await AssertValidationAsync(manual, HttpStatusCode.UnprocessableEntity);
    var errors = manualProblem.GetProperty("errors");
    Assert.Contains("entre 2 y 120", errors.GetProperty("customerName")[0].GetString());
    Assert.Contains("40 caracteres", errors.GetProperty("customerPhone")[0].GetString());
    Assert.Contains("2000 caracteres", errors.GetProperty("notes")[0].GetString());
    Assert.DoesNotContain("Parameter", manualProblem.GetRawText());
  }

  [Fact]
  public async Task RegisterWithA262CharacterEmail_Answers422InSpanish()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    using var response = await context.Client.PostAsJsonAsync(
        "/api/v1/auth/register", new RegisterRequest("Correo Largo", new string('a', 250) + "@example.com", DefaultPassword, null));

    var problem = await AssertValidationAsync(response, HttpStatusCode.UnprocessableEntity);
    Assert.Equal(Barbershop.Domain.Common.EmailRules.InvalidMessage, problem.GetProperty("errors").GetProperty("email")[0].GetString());
  }

  [Fact]
  public async Task ADomainArgumentException_NeverSendsTheDotNetMessage()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    using var response = await context.Client.PostAsync(
        "/api/v1/auth/refresh", new StringContent("{not json", Encoding.UTF8, "application/json"));

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    var body = await response.Content.ReadAsStringAsync();
    Assert.Contains("Hay datos que no son válidos", body);
    Assert.DoesNotContain("must be JSON", body);
  }

  // ── BUG-069 / BUG-081 ──

  [Fact]
  public async Task AnEmptyStatusPatch_Answers422AndKeepsTheProfessionalActive()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAdminAsync(context, "status-admin");
    Authenticate(context.Client, admin);
    var staffProfileId = await SeedStaffAsync(context.Factory, "status-staff");

    using var response = await context.Client.PatchAsync(
        $"/api/v1/admin/staff/{staffProfileId}/status", new StringContent("{}", Encoding.UTF8, "application/json"));

    var problem = await AssertValidationAsync(response, HttpStatusCode.UnprocessableEntity);
    Assert.Equal("Este dato es obligatorio.", problem.GetProperty("errors").GetProperty("isActive")[0].GetString());
    using var scope = context.Factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    Assert.True((await db.StaffProfiles.AsNoTracking().SingleAsync(staff => staff.Id == staffProfileId)).IsActive);
  }

  [Fact]
  public async Task IncomeOrExpenseWithoutAmountOrDate_Answers422()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAdminAsync(context, "money-admin");
    Authenticate(context.Client, admin);

    using (var income = await PostJsonAsync(context.Client, "/api/v1/admin/income",
               $$"""{"serviceId":"{{Guid.NewGuid()}}","staffProfileId":"{{Guid.NewGuid()}}","isPromo":false}"""))
    {
      var errors = (await AssertValidationAsync(income, HttpStatusCode.UnprocessableEntity)).GetProperty("errors");
      Assert.True(errors.TryGetProperty("amount", out _));
      Assert.True(errors.TryGetProperty("occurredOn", out _));
    }

    using var expense = await PostJsonAsync(context.Client, "/api/v1/admin/expenses", """{"name":"Arriendo"}""");
    var expenseErrors = (await AssertValidationAsync(expense, HttpStatusCode.UnprocessableEntity)).GetProperty("errors");
    Assert.True(expenseErrors.TryGetProperty("amount", out _));
    Assert.True(expenseErrors.TryGetProperty("occurredOn", out _));

    using var scope = context.Factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    Assert.Empty(await db.IncomeEntries.ToListAsync());
    Assert.Empty(await db.ExpenseEntries.ToListAsync());
  }

  /// <summary>A value with the wrong type stays a 400 (as before), now with the field and a Spanish message.</summary>
  [Fact]
  public async Task AValueOfTheWrongType_Answers400WithTheField()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAdminAsync(context, "type-admin");
    Authenticate(context.Client, admin);

    using var response = await PostJsonAsync(context.Client, "/api/v1/admin/expenses", """{"name":"Arriendo","amount":"abc","occurredOn":"2026-11-01"}""");

    var problem = await AssertValidationAsync(response, HttpStatusCode.BadRequest);
    Assert.Equal("El valor no tiene el formato esperado.", problem.GetProperty("errors").GetProperty("amount")[0].GetString());
  }

  // ── BUG-097 ──

  [Fact]
  public async Task ANullBusinessDay_Answers422()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAdminAsync(context, "hours-admin");
    Authenticate(context.Client, admin);
    var days = string.Join(",", Enumerable.Range(0, 6).Select(day => $$"""{"dayOfWeek":{{day}},"isOpen":false,"openTime":null,"closeTime":null}"""));

    using var response = await context.Client.PutAsync(
        "/api/v1/admin/content/business-hours", new StringContent($$"""{"days":[{{days}},null]}""", Encoding.UTF8, "application/json"));

    var problem = await AssertValidationAsync(response, HttpStatusCode.UnprocessableEntity);
    Assert.StartsWith("Cada día debe tener datos", problem.GetProperty("errors").GetProperty("days[6]")[0].GetString());
  }

  // ── BUG-072 ──

  [Fact]
  public async Task ConcurrentWrites_NeverAnswer500()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAdminAsync(context, "race-admin");
    var statuses = new List<HttpStatusCode>();

    for (var round = 0; round < 3; round++)
    {
      var roleName = $"Rol {Guid.NewGuid():N}"[..20];
      var roleTasks = Enumerable.Range(0, 4).Select(async _ =>
      {
        using var client = AuthenticatedClient(context.Factory, admin);
        using var response = await client.PostAsJsonAsync("/api/v1/admin/roles", new { name = roleName, permissionIds = Array.Empty<Guid>() });
        return response.StatusCode;
      });

      var hourTasks = Enumerable.Range(0, 4).Select(async index =>
      {
        using var client = AuthenticatedClient(context.Factory, admin);
        var days = Enumerable.Range(0, 7).Select(day => new { dayOfWeek = day, isOpen = (day + index) % 2 == 0, openTime = "09:00", closeTime = "18:00" });
        using var response = await client.PutAsJsonAsync("/api/v1/admin/content/business-hours", new { days });
        return response.StatusCode;
      });

      statuses.AddRange(await Task.WhenAll(roleTasks.Concat(hourTasks)));
    }

    _output.WriteLine(string.Join(", ", statuses));
    Assert.DoesNotContain(HttpStatusCode.InternalServerError, statuses);
    Assert.All(statuses, status => Assert.True(status is HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.Conflict, status.ToString()));
  }

  // ── BUG-089 ──

  [Fact]
  public async Task ErrorResponses_CarryTheCorrelationHeader()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var customer = await RegisterAsync(context.Client, "correlation-customer");
    Authenticate(context.Client, customer);

    using var validation = await PostJsonAsync(context.Client, "/api/v1/customer/appointments", "{}");
    using var forbidden = await context.Client.GetAsync("/api/v1/admin/loyalty/settings");
    using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/customer/profile/photo/" + Guid.NewGuid());
    request.Headers.Add("X-Correlation-ID", "soporte-123");
    using var notFound = await context.Client.SendAsync(request);

    foreach (var response in new[] { validation, forbidden, notFound })
    {
      _output.WriteLine($"{(int)response.StatusCode} {response.RequestMessage!.RequestUri}");
      Assert.True((int)response.StatusCode >= 400);
      Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values), $"No header on {(int)response.StatusCode}");
      Assert.False(string.IsNullOrWhiteSpace(values!.Single()));
    }

    Assert.Equal("soporte-123", notFound.Headers.GetValues("X-Correlation-ID").Single());
  }

  // ── helpers ──

  private async Task<JsonElement> AssertValidationAsync(HttpResponseMessage response, HttpStatusCode expected)
  {
    var body = await response.Content.ReadAsStringAsync();
    _output.WriteLine($"{(int)response.StatusCode} {body}");
    Assert.Equal(expected, response.StatusCode);
    Assert.True(response.Headers.Contains("X-Correlation-ID"));
    return JsonDocument.Parse(body).RootElement.Clone();
  }

  private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, string json)
      => client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

  private static async Task<Guid> SeedStaffAsync(IntegrationTestFactory factory, string label)
  {
    using var scope = factory.Services.CreateScope();
    var staff = scope.ServiceProvider.GetRequiredService<IAdminStaffService>();
    var created = await staff.CreateAsync(new AdminStaffCreateRequest(
        $"Staff {label}", UniqueEmail(label), $"Staff {label}", DefaultPassword, null, null, 30, null, null, true));
    return created.StaffProfileId;
  }

  private static async Task<string> RegisterAsync(HttpClient client, string label)
  {
    using var response = await client.PostAsJsonAsync(
        "/api/v1/auth/register", new RegisterRequest($"User {label}", UniqueEmail(label), DefaultPassword, null));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
  }

  private static async Task<string> RegisterAdminAsync(TestContext context, string label)
  {
    var email = UniqueEmail(label);
    using (var register = await context.Client.PostAsJsonAsync(
               "/api/v1/auth/register", new RegisterRequest($"Admin {label}", email, DefaultPassword, null)))
    {
      Assert.Equal(HttpStatusCode.Created, register.StatusCode);
    }

    using (var scope = context.Factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var user = await db.Users.Include(candidate => candidate.UserRoles)
          .SingleAsync(candidate => candidate.NormalizedEmail == email.ToUpperInvariant());
      var adminRole = await db.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Admin.ToUpperInvariant());
      user.UserRoles.Add(new UserRole(user.Id, adminRole.Id, DateTime.UtcNow));
      await db.SaveChangesAsync();
    }

    context.Client.DefaultRequestHeaders.Authorization = null;
    using var login = await context.Client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, DefaultPassword));
    Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    return (await login.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
  }

  private static HttpClient AuthenticatedClient(IntegrationTestFactory factory, string token)
  {
    var client = factory.CreateClient();
    Authenticate(client, token);
    return client;
  }

  private static void Authenticate(HttpClient client, string token)
      => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

  private static string UniqueEmail(string label) => $"{label}-{Guid.NewGuid():N}@example.com";

  private async Task<TestContext?> CreateContextAsync()
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return null;
    }

    var factory = new IntegrationTestFactory(_postgres.ConnectionString);
    await factory.ResetDatabaseAsync();
    return new TestContext(factory, factory.CreateClient());
  }

  private sealed class TestContext(IntegrationTestFactory factory, HttpClient client) : IDisposable
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
