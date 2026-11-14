using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Barbershop.Application.Auth;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Auth;

/// <summary>
/// SONDA lote C3 over real HTTP: written emails (BUG-004, BUG-017), password change policy (BUG-018), birth date range
/// (BUG-059), concurrent customer edits (BUG-061) and the public contracts and cache policy (BUG-041, BUG-101).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "AuthAndUserDataHttpIntegration")]
public sealed class AuthAndUserDataHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public AuthAndUserDataHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  // ── BUG-004 / BUG-017 ──

  [Theory]
  [InlineData("a b@c.com")]
  [InlineData("QA Test <usuario@gmail.com>")]
  [InlineData("usuario@localhost")]
  [InlineData("cliente​@example.com")]
  [InlineData("аdmin@example.com")]
  public async Task Register_WithAnInvalidOrLookAlikeEmail_Answers422(string email)
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    using var response = await context.Client.PostAsJsonAsync(
        "/api/v1/auth/register", new RegisterRequest("Correo Raro", email, DefaultPassword, null));

    var problem = await ReadAsync(response, HttpStatusCode.UnprocessableEntity);
    Assert.StartsWith("Escribe un correo válido", problem.GetProperty("errors").GetProperty("email")[0].GetString());
  }

  [Fact]
  public async Task AdminCustomersAndStaff_RejectAnEmailWithADisplayName()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    Authenticate(context.Client, await RegisterAdminAsync(context, "email-admin"));

    using (var customer = await context.Client.PostAsJsonAsync(
               "/api/v1/admin/customers", new { fullName = "Cliente QA", phone = "3001234567", email = "QA <qa@example.com>" }))
    {
      await ReadAsync(customer, HttpStatusCode.UnprocessableEntity);
    }

    using var staff = await context.Client.PostAsJsonAsync("/api/v1/admin/staff", new
    {
      fullName = "Profesional QA", email = "QA <staff-qa@example.com>", displayName = "Profe QA", initialPassword = DefaultPassword,
      phoneNumber = (string?)null, bio = (string?)null, defaultAppointmentDurationMinutes = 30,
      photoMediaAssetId = (Guid?)null, tipsQrMediaAssetId = (Guid?)null, isActive = true
    });
    var problem = await ReadAsync(staff, HttpStatusCode.UnprocessableEntity);
    Assert.True(problem.GetProperty("errors").TryGetProperty("email", out _));
  }

  // ── BUG-061 (and the existing-email rule of BUG-004) ──

  [Fact]
  public async Task EditingACustomer_WithAStaleVersion_Answers409_AndAnUnchangedLegacyEmailIsKept()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    Authenticate(context.Client, await RegisterAdminAsync(context, "edit-admin"));
    var customerId = await SeedCustomerWithLegacyEmailAsync(context.Factory, "legacy@localhost");

    var opened = await GetCustomerAsync(context.Client, customerId);
    var version = opened.GetProperty("version").GetUInt32();

    // First tab saves: the legacy email is left as it is, so the strict rule does not block the edit.
    using (var first = await context.Client.PutAsJsonAsync(
               $"/api/v1/admin/customers/{customerId}", new { fullName = "Primera Pestaña", phone = "3001234567", email = "legacy@localhost", version }))
    {
      var saved = await ReadAsync(first, HttpStatusCode.OK);
      Assert.NotEqual(version, saved.GetProperty("version").GetUInt32());
    }

    // Second tab, opened at the same version, is refused instead of overwriting the first.
    using (var second = await context.Client.PutAsJsonAsync(
               $"/api/v1/admin/customers/{customerId}", new { fullName = "Segunda Pestaña", phone = "3001234567", email = "legacy@localhost", version }))
    {
      var conflict = await ReadAsync(second, HttpStatusCode.Conflict);
      Assert.Contains("modificado por otra operación", conflict.GetProperty("detail").GetString());
    }

    Assert.Equal("Primera Pestaña", (await GetCustomerAsync(context.Client, customerId)).GetProperty("fullName").GetString());

    using (var noVersion = await context.Client.PutAsJsonAsync(
               $"/api/v1/admin/customers/{customerId}", new { fullName = "Sin Versión", phone = "3001234567", email = "legacy@localhost" }))
    {
      await ReadAsync(noVersion, HttpStatusCode.UnprocessableEntity);
    }

    // Changing the email to an invalid one is refused.
    var current = (await GetCustomerAsync(context.Client, customerId)).GetProperty("version").GetUInt32();
    using var changed = await context.Client.PutAsJsonAsync(
        $"/api/v1/admin/customers/{customerId}", new { fullName = "Primera Pestaña", phone = "3001234567", email = "otro@localhost", version = current });
    await ReadAsync(changed, HttpStatusCode.UnprocessableEntity);
  }

  // ── BUG-018 ──

  [Fact]
  public async Task ChangingThePassword_FollowsTheRegistrationPolicy_AndRefusesTheCurrentOne()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    Authenticate(context.Client, await RegisterAsync(context.Client, "password-customer"));

    using (var tooLong = await context.Client.PostAsJsonAsync(
               "/api/v1/customer/profile/password", new { currentPassword = DefaultPassword, newPassword = new string('x', 200) }))
    {
      var problem = await ReadAsync(tooLong, HttpStatusCode.UnprocessableEntity);
      Assert.Contains("entre 8 y 128", problem.GetProperty("errors").GetProperty("newPassword")[0].GetString());
    }

    using (var same = await context.Client.PostAsJsonAsync(
               "/api/v1/customer/profile/password", new { currentPassword = DefaultPassword, newPassword = DefaultPassword }))
    {
      var problem = await ReadAsync(same, HttpStatusCode.UnprocessableEntity);
      Assert.Contains("distinta de la actual", problem.GetProperty("errors").GetProperty("newPassword")[0].GetString());
    }

    // The change itself still answers 200 with a new session (0cd70b2).
    using var valid = await context.Client.PostAsJsonAsync(
        "/api/v1/customer/profile/password", new { currentPassword = DefaultPassword, newPassword = "OtraClave456!" });
    var session = await ReadAsync(valid, HttpStatusCode.OK);
    Assert.False(string.IsNullOrWhiteSpace(session.GetProperty("accessToken").GetString()));
  }

  // ── BUG-059 ──

  [Fact]
  public async Task TheBirthDate_MustBeBetween1900AndToday()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    Authenticate(context.Client, await RegisterAsync(context.Client, "birth-customer"));
    var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));

    foreach (var invalid in new[] { today.AddDays(1), new DateOnly(2030, 1, 1), new DateOnly(1899, 12, 31) })
    {
      using var response = await PatchProfileAsync(context.Client, invalid);
      var problem = await ReadAsync(response, HttpStatusCode.UnprocessableEntity);
      Assert.True(problem.GetProperty("errors").TryGetProperty("dateOfBirth", out _));
    }

    foreach (var valid in new[] { new DateOnly(1900, 1, 1), new DateOnly(1990, 5, 20), today })
    {
      using var response = await PatchProfileAsync(context.Client, valid);
      await ReadAsync(response, HttpStatusCode.OK);
    }
  }

  // ── BUG-101 ──

  [Fact]
  public async Task PublicContent_CarriesOnlyPresentationFields_AndAShortPublicCache()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var adminToken = await RegisterAdminAsync(context, "content-admin");
    Authenticate(context.Client, adminToken);
    using (var created = await context.Client.PostAsJsonAsync("/api/v1/admin/content/banners", new
           {
             title = "Promo de noviembre", subtitle = (string?)null, imageMediaAssetId = (Guid?)null, linkUrl = (string?)null,
             sortOrder = 1, isActive = true, startsAtUtc = (DateTime?)null, endsAtUtc = (DateTime?)null
           }))
    {
      await ReadAsync(created, HttpStatusCode.Created);
    }

    using (var admin = await context.Client.GetAsync("/api/v1/admin/content/banners"))
    {
      Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
      Assert.True(admin.Headers.CacheControl?.NoStore, admin.Headers.CacheControl?.ToString());
    }

    context.Client.DefaultRequestHeaders.Authorization = null;
    foreach (var (path, forbidden) in new[]
             {
               ("/api/v1/public/content/banners", new[] { "imageMediaAssetId", "isActive", "sortOrder", "createdAtUtc", "updatedAtUtc" }),
               ("/api/v1/public/content/branding", new[] { "logoMediaAssetId", "appIconMediaAssetId", "updatedAtUtc" }),
               ("/api/v1/public/content/ticker-items", new[] { "isActive", "sortOrder", "createdAtUtc" }),
               ("/api/v1/public/content/landing", new[] { "updatedAtUtc" })
             })
    {
      using var response = await context.Client.GetAsync(path);
      var body = await response.Content.ReadAsStringAsync();
      _output.WriteLine($"{path}: {body}");
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      Assert.Equal("public, max-age=60", response.Headers.CacheControl?.ToString());
      Assert.All(forbidden, field => Assert.DoesNotContain($"\"{field}\"", body, StringComparison.OrdinalIgnoreCase));
    }

    using (var banners = await context.Client.GetAsync("/api/v1/public/content/banners"))
    {
      Assert.Contains("Promo de noviembre", await banners.Content.ReadAsStringAsync());
    }

    using (var slots = await context.Client.GetAsync(
               $"/api/v1/public/staff/{Guid.NewGuid()}/availability/slots?from=2030-01-01&to=2030-01-01&generalServiceId={Guid.NewGuid()}"))
    {
      Assert.True(slots.Headers.CacheControl?.NoStore, slots.Headers.CacheControl?.ToString());
    }

    using var notFound = await context.Client.GetAsync($"/api/v1/public/staff/{Guid.NewGuid()}");
    Assert.True(notFound.Headers.CacheControl?.NoStore, notFound.Headers.CacheControl?.ToString());
  }

  // ── helpers ──

  private async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode expected)
  {
    var body = await response.Content.ReadAsStringAsync();
    _output.WriteLine($"{(int)response.StatusCode} {body}");
    Assert.Equal(expected, response.StatusCode);
    return string.IsNullOrWhiteSpace(body) ? default : JsonDocument.Parse(body).RootElement.Clone();
  }

  private async Task<JsonElement> GetCustomerAsync(HttpClient client, Guid customerId)
  {
    using var response = await client.GetAsync($"/api/v1/admin/customers/{customerId}");
    return await ReadAsync(response, HttpStatusCode.OK);
  }

  private static Task<HttpResponseMessage> PatchProfileAsync(HttpClient client, DateOnly dateOfBirth)
      => client.PatchAsJsonAsync("/api/v1/customer/profile", new { fullName = "Cliente Fecha", phoneNumber = (string?)null, dateOfBirth });

  /// <summary>A profile saved before the strict rule (its domain still accepts "@localhost").</summary>
  private static async Task<Guid> SeedCustomerWithLegacyEmailAsync(IntegrationTestFactory factory, string email)
  {
    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var createdBy = await db.Users.Select(user => user.Id).FirstAsync();
    var customer = new CustomerProfile("Cliente Antiguo", "3001234567", email, createdBy, DateTime.UtcNow);
    db.Customers.Add(customer);
    await db.SaveChangesAsync();
    return customer.Id;
  }

  private static async Task<string> RegisterAsync(HttpClient client, string label)
  {
    using var response = await client.PostAsJsonAsync(
        "/api/v1/auth/register", new RegisterRequest($"User {label}", $"{label}-{Guid.NewGuid():N}@example.com", DefaultPassword, null));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
  }

  private static async Task<string> RegisterAdminAsync(TestContext context, string label)
  {
    var email = $"{label}-{Guid.NewGuid():N}@example.com";
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

    using var login = await context.Client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, DefaultPassword));
    Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    return (await login.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
  }

  private static void Authenticate(HttpClient client, string token)
      => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

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
