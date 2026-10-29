using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Application.Services.Admin;
using Barbershop.Domain.Memberships;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Memberships;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "AdminMembershipPlansHttpIntegration")]
public sealed class AdminMembershipPlansHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";
  private const string Terms = "La membresía es personal e intransferible.";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public AdminMembershipPlansHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task Admin_CanRunTheFullPlanLifecycle()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAndPromoteAdminAsync(context, "plans");
    AuthenticateAs(context.Client, admin.AccessToken);
    var haircut = await CreateServiceAsync(context.Client, "Corte", 25000);
    var beard = await CreateServiceAsync(context.Client, "Barba", 15000);

    var request = new MembershipPlanUpsertRequest(
        "Membresía Oro", "Diez servicios", 250000, "Pool", 10, 90, Terms, null,
        [new(haircut.Id, null), new(beard.Id, null)],
        [
          new(null, "Service", haircut.Id, null, 1, "FirstPurchaseOfPlan"),
          new(null, "Physical", null, "Cera para cabello", 1, "EveryPurchase"),
        ]);

    using var created = await context.Client.PostAsJsonAsync("/api/v1/admin/membership-plans", request);
    Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    var plan = (await created.Content.ReadFromJsonAsync<MembershipPlanView>())!;
    Assert.Equal("Pool", plan.Mode);
    Assert.Equal(2, plan.Services.Count);
    Assert.Equal(["Service", "Physical"], plan.GiftRules.Select(rule => rule.Kind));

    using var duplicate = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/membership-plans", request with { Name = "membresía oro" });
    Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

    using var invalid = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/membership-plans", request with { Name = "Inválida", PoolQuantity = 0, DefaultDurationDays = 0 });
    Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);

    var physicalRuleId = plan.GiftRules.Single(rule => rule.Kind == "Physical").Id;
    using var updated = await context.Client.PutAsJsonAsync(
        $"/api/v1/admin/membership-plans/{plan.Id}",
        request with
        {
          Mode = "PerService",
          PoolQuantity = null,
          Services = [new(haircut.Id, 8), new(beard.Id, 4)],
          GiftRules = [new(physicalRuleId, "Physical", null, "Champú", 2, "EveryPurchase")],
        });
    Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
    var perService = (await updated.Content.ReadFromJsonAsync<MembershipPlanView>())!;
    Assert.Equal("PerService", perService.Mode);
    Assert.Null(perService.PoolQuantity);
    Assert.Equal(physicalRuleId, Assert.Single(perService.GiftRules).Id);

    using var deactivated = await context.Client.PatchAsJsonAsync(
        $"/api/v1/admin/membership-plans/{plan.Id}/status", new MembershipPlanStatusUpdateRequest(false));
    Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
    Assert.False((await deactivated.Content.ReadFromJsonAsync<MembershipPlanView>())!.IsActive);

    using var deleted = await context.Client.DeleteAsync($"/api/v1/admin/membership-plans/{plan.Id}");
    Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    var list = await context.Client.GetFromJsonAsync<List<MembershipPlanView>>("/api/v1/admin/membership-plans");
    Assert.DoesNotContain(list!, listed => listed.Id == plan.Id);

    // The removed service gift rule is kept (soft-deleted) for the sold memberships of later stages.
    using var scope = context.Factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    Assert.Equal(2, await db.MembershipPlanGiftRules.CountAsync(rule => rule.MembershipPlanId == plan.Id));
  }

  [Fact]
  public async Task GiftRules_PersistAcrossRepeatedEditsAndStatusChanges_InPostgreSql()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAndPromoteAdminAsync(context, "gift-persistence");
    AuthenticateAs(context.Client, admin.AccessToken);
    var haircut = await CreateServiceAsync(context.Client, "Corte", 25000);
    var beard = await CreateServiceAsync(context.Client, "Barba", 15000);
    var baseRequest = new MembershipPlanUpsertRequest(
        "Oro", null, 250000, "Pool", 10, 90, Terms, null, [new(haircut.Id, null), new(beard.Id, null)], []);

    var plan = await SendAsync(context.Client, HttpMethod.Post, "/api/v1/admin/membership-plans", baseRequest with
    {
      GiftRules =
      [
        new(null, "Service", haircut.Id, null, 1, "FirstPurchaseOfPlan"),
        new(null, "Physical", null, "Cera", 1, "EveryPurchase"),
        new(null, "Physical", null, "Gorra", 2, "FirstPurchaseOfPlan"),
      ],
    });
    var originalIds = plan.GiftRules.Select(rule => rule.Id).ToArray();

    // Five consecutive saves: reorder once, then resubmit unchanged; ids and order must hold.
    var reordered = originalIds.Reverse().ToArray();
    var current = plan;
    for (var save = 0; save < 5; save++)
    {
      var order = save == 0 ? reordered : current.GiftRules.Select(rule => rule.Id).ToArray();
      current = await SendAsync(context.Client, HttpMethod.Put, $"/api/v1/admin/membership-plans/{plan.Id}", baseRequest with
      {
        GiftRules = order.Select(id => ToRequest(current.GiftRules.Single(rule => rule.Id == id))).ToArray(),
      });
      Assert.Equal(reordered, current.GiftRules.Select(rule => rule.Id));
    }

    using (var off = await context.Client.PatchAsJsonAsync(
        $"/api/v1/admin/membership-plans/{plan.Id}/status", new MembershipPlanStatusUpdateRequest(false)))
    {
      Assert.Equal(HttpStatusCode.OK, off.StatusCode);
    }

    using (var on = await context.Client.PatchAsJsonAsync(
        $"/api/v1/admin/membership-plans/{plan.Id}/status", new MembershipPlanStatusUpdateRequest(true)))
    {
      Assert.Equal(HttpStatusCode.OK, on.StatusCode);
    }

    var reloaded = await context.Client.GetFromJsonAsync<MembershipPlanView>($"/api/v1/admin/membership-plans/{plan.Id}");
    Assert.True(reloaded!.IsActive);
    Assert.Equal(reordered, reloaded.GiftRules.Select(rule => rule.Id));
    Assert.Equal(current.GiftRules, reloaded.GiftRules);
    Assert.Equal(2, reloaded.Services.Count);

    using var scope = context.Factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var stored = await db.MembershipPlanGiftRules.Where(rule => rule.MembershipPlanId == plan.Id).ToListAsync();
    Assert.Equal(3, stored.Count);
    Assert.DoesNotContain(stored, rule => rule.IsDeleted);
    Assert.Equal([0, 1, 2], stored.OrderBy(rule => rule.Position).Select(rule => rule.Position));
  }

  [Fact]
  public async Task PlanEndpoints_AsPlainCustomer_ReturnForbidden_AndUnauthenticated_ReturnUnauthorized()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    using var anonymous = await context.Client.GetAsync("/api/v1/admin/membership-plans");
    Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

    var customer = await RegisterCustomerAsync(context.Client, "plans-forbidden");
    AuthenticateAs(context.Client, customer.AccessToken);

    using var list = await context.Client.GetAsync("/api/v1/admin/membership-plans");
    Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);

    using var create = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/membership-plans",
        new MembershipPlanUpsertRequest("X", null, 1, "Pool", 1, 1, Terms, null, [], []));
    Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
  }

  [Fact]
  public async Task DatabaseConstraints_RejectModeAndGiftShapeViolations_EvenWhenTheServiceIsBypassed()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var serviceId = await SeedServiceAsync(context.Factory);
    var plan = await SeedPlanAsync(context.Factory, serviceId);

    await AssertSqlFailsAsync(context.Factory,
        $"UPDATE public.membership_plans SET \"PoolQuantity\" = NULL WHERE \"Id\" = '{plan}'");
    await AssertSqlFailsAsync(context.Factory,
        $"UPDATE public.membership_plans SET \"DefaultDurationDays\" = 0 WHERE \"Id\" = '{plan}'");
    await AssertSqlFailsAsync(context.Factory,
        $"INSERT INTO public.membership_plan_services (\"MembershipPlanId\", \"ServiceId\", \"Quantity\") VALUES ('{plan}', '{serviceId}', NULL)");
    await AssertSqlFailsAsync(context.Factory,
        "INSERT INTO public.membership_plan_gift_rules (\"Id\", \"MembershipPlanId\", \"Kind\", \"ServiceId\", \"Description\", \"Quantity\", \"Condition\", \"Position\", \"IsDeleted\", \"CreatedAt\") "
        + $"VALUES ('{Guid.NewGuid()}', '{plan}', 'Physical', NULL, NULL, 1, 'EveryPurchase', 0, false, now())");
    await AssertSqlFailsAsync(context.Factory,
        "INSERT INTO public.membership_plan_gift_rules (\"Id\", \"MembershipPlanId\", \"Kind\", \"ServiceId\", \"Description\", \"Quantity\", \"Condition\", \"Position\", \"IsDeleted\", \"CreatedAt\") "
        + $"VALUES ('{Guid.NewGuid()}', '{plan}', 'Physical', NULL, 'Gorra', 0, 'EveryPurchase', 0, false, now())");
  }

  [Fact]
  public async Task ConcurrencyToken_RejectsStalePlanWrites()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var serviceId = await SeedServiceAsync(context.Factory);
    var planId = await SeedPlanAsync(context.Factory, serviceId);

    using var scopeA = context.Factory.Services.CreateScope();
    using var scopeB = context.Factory.Services.CreateScope();
    var dbA = scopeA.ServiceProvider.GetRequiredService<AppDbContext>();
    var dbB = scopeB.ServiceProvider.GetRequiredService<AppDbContext>();
    var copyA = await dbA.MembershipPlans.SingleAsync(plan => plan.Id == planId);
    var copyB = await dbB.MembershipPlans.SingleAsync(plan => plan.Id == planId);

    copyA.Deactivate(DateTime.UtcNow);
    await dbA.SaveChangesAsync();

    copyB.MarkDeleted(DateTime.UtcNow);
    await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
  }

  private static async Task<MembershipPlanView> SendAsync(HttpClient client, HttpMethod method, string url, MembershipPlanUpsertRequest body)
  {
    using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
    using var response = await client.SendAsync(request);
    Assert.True(response.IsSuccessStatusCode, $"{method} {url} returned {(int)response.StatusCode}");
    return (await response.Content.ReadFromJsonAsync<MembershipPlanView>())!;
  }

  private static MembershipPlanGiftRuleRequest ToRequest(MembershipPlanGiftRuleView rule)
      => new(rule.Id, rule.Kind, rule.ServiceId, rule.Description, rule.Quantity, rule.Condition);

  private static async Task AssertSqlFailsAsync(IntegrationTestFactory factory, string sql)
  {
    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(sql));
  }

  private static async Task<Guid> SeedServiceAsync(IntegrationTestFactory factory)
  {
    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var service = new Barbershop.Domain.Services.Service("Corte", 25000, DateTime.UtcNow);
    db.Services.Add(service);
    await db.SaveChangesAsync();
    return service.Id;
  }

  private static async Task<Guid> SeedPlanAsync(IntegrationTestFactory factory, Guid serviceId)
  {
    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var plan = new MembershipPlan("Oro", null, 100000, MembershipPlanMode.Pool, 5, 30, Terms, null, DateTime.UtcNow);
    plan.Services.Add(new MembershipPlanIncludedService(plan.Id, serviceId, null));
    db.MembershipPlans.Add(plan);
    await db.SaveChangesAsync();
    return plan.Id;
  }

  private static async Task<ServiceView> CreateServiceAsync(HttpClient client, string name, int price)
  {
    using var response = await client.PostAsJsonAsync("/api/v1/admin/services", new ServiceCreateRequest(name, price, 30));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<ServiceView>())!;
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
