using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Barbershop.Application.Auth;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Application.Services.Admin;
using Barbershop.Domain.Common;
using Barbershop.Domain.Memberships;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Memberships;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "MembershipLedgerHttpIntegration")]
public sealed class MembershipLedgerHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";
  private const string Terms = "La membresía es personal e intransferible.";
  private static readonly DateOnly Today = DateOnly.FromDateTime(BogotaClock.ToLocal(DateTime.UtcNow));

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public MembershipLedgerHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task Admin_ConsumesAndReverses_OverHttp_AndOnlyAdminsCan()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAndPromoteAdminAsync(context, "ledger");
    var seed = await SeedActiveMembershipAsync(context.Factory, admin.UserId);
    AuthenticateAs(context.Client, admin.AccessToken);

    using var consumed = await context.Client.PostAsJsonAsync(
        $"/api/v1/admin/memberships/{seed.MembershipId}/consumptions",
        new MembershipConsumptionRequest(seed.BeardId, 3, Today, null, Guid.NewGuid()));
    Assert.Equal(HttpStatusCode.OK, consumed.StatusCode);
    var membership = (await consumed.Content.ReadFromJsonAsync<CustomerMembershipDetailView>())!;
    var consumption = Assert.Single(membership.Consumptions);
    Assert.Equal(["Gift:2", "Purchased:1"], consumption.Parts.Select(part => $"{part.Origin}:{part.Quantity}"));

    // Decimal quantities never reach the service: the body does not bind to an integer.
    using var decimalBody = new StringContent(
        $$"""{"serviceId":"{{seed.HaircutId}}","quantity":1.5,"serviceDate":"{{Today:yyyy-MM-dd}}","staffProfileId":null,"idempotencyKey":"{{Guid.NewGuid()}}"}""",
        Encoding.UTF8,
        "application/json");
    using var decimalQuantity = await context.Client.PostAsync($"/api/v1/admin/memberships/{seed.MembershipId}/consumptions", decimalBody);
    Assert.Equal(HttpStatusCode.BadRequest, decimalQuantity.StatusCode);

    using var zero = await context.Client.PostAsJsonAsync(
        $"/api/v1/admin/memberships/{seed.MembershipId}/consumptions",
        new MembershipConsumptionRequest(seed.HaircutId, 0, Today, null, Guid.NewGuid()));
    Assert.Equal(HttpStatusCode.UnprocessableEntity, zero.StatusCode);

    var reverseUrl = $"/api/v1/admin/memberships/{seed.MembershipId}/consumptions/{consumption.Id}/reverse";
    using var reversed = await context.Client.PostAsJsonAsync(reverseUrl, new MembershipConsumptionReverseRequest("Registrado por error"));
    Assert.Equal(HttpStatusCode.OK, reversed.StatusCode);
    using var twice = await context.Client.PostAsJsonAsync(reverseUrl, new MembershipConsumptionReverseRequest("Otra vez"));
    Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);

    var customer = await RegisterCustomerAsync(context.Client, "ledger");
    AuthenticateAs(context.Client, customer.AccessToken);
    using var asCustomer = await context.Client.PostAsJsonAsync(
        $"/api/v1/admin/memberships/{seed.MembershipId}/consumptions",
        new MembershipConsumptionRequest(seed.HaircutId, 1, Today, null, Guid.NewGuid()));
    Assert.Equal(HttpStatusCode.Forbidden, asCustomer.StatusCode);
    using var reverseAsCustomer = await context.Client.PostAsJsonAsync(reverseUrl, new MembershipConsumptionReverseRequest("Intento"));
    Assert.Equal(HttpStatusCode.Forbidden, reverseAsCustomer.StatusCode);

    context.Client.DefaultRequestHeaders.Authorization = null;
    using var anonymous = await context.Client.PostAsJsonAsync(
        $"/api/v1/admin/memberships/{seed.MembershipId}/consumptions",
        new MembershipConsumptionRequest(seed.HaircutId, 1, Today, null, Guid.NewGuid()));
    Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

    // Only the admin's consumption and its reversal exist; no income was generated.
    using var scope = context.Factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    Assert.Equal(1, await db.MembershipConsumptions.CountAsync());
    Assert.Equal(0, await db.IncomeEntries.CountAsync());
  }

  [Fact]
  public async Task ConcurrentConsumptions_NeverTakeMoreThanTheBalance()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var adminId = await SeedAdminUserAsync(context.Factory);
    var seed = await SeedActiveMembershipAsync(context.Factory, adminId, gift: false);

    // 16 admins register one haircut each at the same moment on a pool of 10.
    var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        TryConsumeAsync(context.Factory, adminId, seed.MembershipId, new MembershipConsumptionRequest(seed.HaircutId, 1, Today, null, Guid.NewGuid())))));

    Assert.Equal(10, results.Count(result => result));
    using var scope = context.Factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    Assert.Equal(10, await db.MembershipConsumptions.CountAsync());
    Assert.Equal(0, await AvailableInDatabaseAsync(db, seed.MembershipId));
  }

  [Fact]
  public async Task ConcurrentRetries_WithTheSameKey_StoreASingleConsumption()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var adminId = await SeedAdminUserAsync(context.Factory);
    var seed = await SeedActiveMembershipAsync(context.Factory, adminId, gift: false);
    var request = new MembershipConsumptionRequest(seed.HaircutId, 2, Today, null, Guid.NewGuid());

    var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        TryConsumeAsync(context.Factory, adminId, seed.MembershipId, request))));

    Assert.All(results, Assert.True); // every retry gets the same, already registered consumption
    using var scope = context.Factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    Assert.Equal(1, await db.MembershipConsumptions.CountAsync());
    Assert.Equal(8, await AvailableInDatabaseAsync(db, seed.MembershipId));
  }

  [Fact]
  public async Task ConcurrentReversals_OfOneConsumption_ApplyOnce()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var adminId = await SeedAdminUserAsync(context.Factory);
    var seed = await SeedActiveMembershipAsync(context.Factory, adminId, gift: false);
    Guid consumptionId;
    using (var scope = context.Factory.Services.CreateScope())
    {
      consumptionId = (await scope.ServiceProvider.GetRequiredService<IAdminMembershipLedgerService>().ConsumeAsync(
          adminId, seed.MembershipId, new MembershipConsumptionRequest(seed.HaircutId, 4, Today, null, Guid.NewGuid()))).Consumptions.Single().Id;
    }

    var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
    {
      using var scope = context.Factory.Services.CreateScope();
      try
      {
        await scope.ServiceProvider.GetRequiredService<IAdminMembershipLedgerService>().ReverseAsync(
            adminId, seed.MembershipId, consumptionId, new MembershipConsumptionReverseRequest("Registrado por error"));
        return true;
      }
      catch (ConflictException)
      {
        return false;
      }
    })));

    Assert.Equal(1, results.Count(result => result));
    using var check = context.Factory.Services.CreateScope();
    var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
    Assert.Equal(1, await db.MembershipConsumptionReversals.CountAsync());
    Assert.Equal(10, await AvailableInDatabaseAsync(db, seed.MembershipId));
  }

  [Fact]
  public async Task LedgerRows_AreAppendOnly_AndTheStoredBalancesMatchTheView()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var adminId = await SeedAdminUserAsync(context.Factory);
    var seed = await SeedActiveMembershipAsync(context.Factory, adminId);
    CustomerMembershipDetailView view;
    using (var scope = context.Factory.Services.CreateScope())
    {
      var ledger = scope.ServiceProvider.GetRequiredService<IAdminMembershipLedgerService>();
      var first = await ledger.ConsumeAsync(adminId, seed.MembershipId, new MembershipConsumptionRequest(seed.BeardId, 3, Today, null, Guid.NewGuid()));
      await ledger.ConsumeAsync(adminId, seed.MembershipId, new MembershipConsumptionRequest(seed.HaircutId, 4, Today, null, Guid.NewGuid()));
      view = await ledger.ReverseAsync(adminId, seed.MembershipId, first.Consumptions.Single(c => c.ServiceId == seed.BeardId).Id,
          new MembershipConsumptionReverseRequest("Barba registrada de más"));
    }

    using var sqlScope = context.Factory.Services.CreateScope();
    var db = sqlScope.ServiceProvider.GetRequiredService<AppDbContext>();

    // Balances computed straight from the stored rows agree with what the admin sees.
    var stored = await db.Database.SqlQuery<StoredBalance>($"""
        SELECT a."Id" AS "AllowanceId",
               a."GrantedQuantity" - COALESCE(SUM(CASE m."Type" WHEN 'Consumption' THEN m."Quantity" ELSE -m."Quantity" END), 0)::int AS "Available"
        FROM public.membership_allowances a
        LEFT JOIN public.membership_movements m ON m."MembershipAllowanceId" = a."Id"
        WHERE a."CustomerMembershipId" = {seed.MembershipId}
        GROUP BY a."Id", a."GrantedQuantity"
        """).ToListAsync();
    Assert.Equal(
        view.Allowances.OrderBy(line => line.Id).Select(line => $"{line.Id}:{line.AvailableQuantity}"),
        stored.OrderBy(line => line.AllowanceId).Select(line => $"{line.AllowanceId}:{line.Available}"));
    Assert.Equal(["Purchased:6/10", "Gift:2/2"], view.Allowances.Select(line => $"{line.Origin}:{line.AvailableQuantity}/{line.GrantedQuantity}"));

    // The database itself refuses edits or deletions of the ledger.
    await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("UPDATE public.membership_movements SET \"Quantity\" = 1"));
    await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM public.membership_consumption_reversals"));
    await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("UPDATE public.membership_consumptions SET \"Quantity\" = 9"));
    await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM public.membership_consumptions"));
    // ...and a second reversal of the same consumption, or a zero movement.
    var reversal = await db.MembershipConsumptionReversals.AsNoTracking().SingleAsync();
    await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.membership_consumption_reversals ("Id", "MembershipConsumptionId", "Reason", "PerformedByUserId", "PerformedAt")
        VALUES ({Guid.NewGuid()}, {reversal.MembershipConsumptionId}, 'Duplicado', {adminId}, now())
        """));
    Assert.Equal(5, await db.MembershipMovements.CountAsync()); // beard: gift 2 + purchased 1; haircut: 1; reversal: 2
  }

  private sealed record StoredBalance(Guid AllowanceId, int Available);

  private sealed record SeededMembership(Guid MembershipId, Guid HaircutId, Guid BeardId);

  private static async Task<int> AvailableInDatabaseAsync(AppDbContext db, Guid membershipId)
  {
    var granted = await db.MembershipAllowances.Where(line => line.CustomerMembershipId == membershipId).SumAsync(line => line.GrantedQuantity);
    var used = await db.MembershipMovements
        .Where(movement => movement.CustomerMembershipId == membershipId)
        .SumAsync(movement => movement.Type == MembershipMovementType.Consumption ? movement.Quantity : -movement.Quantity);
    return granted - used;
  }

  private static async Task<bool> TryConsumeAsync(IntegrationTestFactory factory, Guid adminId, Guid membershipId, MembershipConsumptionRequest request)
  {
    using var scope = factory.Services.CreateScope();
    try
    {
      await scope.ServiceProvider.GetRequiredService<IAdminMembershipLedgerService>().ConsumeAsync(adminId, membershipId, request);
      return true;
    }
    catch (ValidationProblemException)
    {
      return false;
    }
  }

  /// <summary>An active pool membership of 10 for haircut and beard, plus (optionally) 2 gifted beards.</summary>
  private static async Task<SeededMembership> SeedActiveMembershipAsync(IntegrationTestFactory factory, Guid adminId, bool gift = true)
  {
    using var scope = factory.Services.CreateScope();
    var services = scope.ServiceProvider.GetRequiredService<IAdminServicesService>();
    var haircut = await services.CreateAsync(new ServiceCreateRequest($"Corte {Guid.NewGuid():N}"[..20], 25000, 30));
    var beard = await services.CreateAsync(new ServiceCreateRequest($"Barba {Guid.NewGuid():N}"[..20], 15000, 30));
    var customer = await scope.ServiceProvider.GetRequiredService<IAdminCustomersService>()
        .CreateAsync(adminId, new CustomerCreateRequest("Carlos Pérez", "3001234567", $"{Guid.NewGuid():N}@example.com"));
    var plan = await scope.ServiceProvider.GetRequiredService<IAdminMembershipPlansService>().CreateAsync(new MembershipPlanUpsertRequest(
        $"Oro {Guid.NewGuid():N}"[..20], null, 250000, "Pool", 10, 90, Terms, null,
        [new(haircut.Id, null), new(beard.Id, null)],
        gift ? [new(null, "Service", beard.Id, null, 2, "EveryPurchase")] : []));
    var membership = await scope.ServiceProvider.GetRequiredService<IAdminMembershipsService>()
        .SellAsync(adminId, customer.Id, new MembershipSaleRequest(plan.Id, Today, ActivateNow: true));
    return new SeededMembership(membership.Id, haircut.Id, beard.Id);
  }

  private static async Task<Guid> SeedAdminUserAsync(IntegrationTestFactory factory)
  {
    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var user = new User("Admin Semilla", $"seed-{Guid.NewGuid():N}@example.com", "hash", DateTime.UtcNow);
    db.Users.Add(user);
    await db.SaveChangesAsync();
    return user.Id;
  }

  private async Task<AuthSession> RegisterAndPromoteAdminAsync(TestHttpContext context, string label)
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
      var user = await db.Users
          .Include(candidate => candidate.UserRoles)
          .SingleAsync(candidate => candidate.NormalizedEmail == email.ToUpperInvariant());
      var adminRole = await db.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Admin.ToUpperInvariant());
      if (user.UserRoles.All(assignment => assignment.RoleId != adminRole.Id))
      {
        user.UserRoles.Add(new UserRole(user.Id, adminRole.Id, DateTime.UtcNow));
      }
      await db.SaveChangesAsync();
    }

    using var login = await context.Client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, DefaultPassword));
    Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    var payload = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;
    return new AuthSession(payload.User.Id, payload.AccessToken);
  }

  private static async Task<AuthSession> RegisterCustomerAsync(HttpClient client, string label)
  {
    using var response = await client.PostAsJsonAsync(
        "/api/v1/auth/register",
        new RegisterRequest($"Customer {label}", UniqueEmail($"customer-{label}"), DefaultPassword, null));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    var payload = (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    return new AuthSession(payload.User.Id, payload.AccessToken);
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

  private static void AuthenticateAs(HttpClient client, string accessToken)
      => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

  private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

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
