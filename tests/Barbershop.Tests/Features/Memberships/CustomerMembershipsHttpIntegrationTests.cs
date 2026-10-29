using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Application.Memberships.Portal;
using Barbershop.Application.Services.Admin;
using Barbershop.Domain.Common;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Memberships;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "CustomerMembershipsHttpIntegration")]
public sealed class CustomerMembershipsHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";
  private const string Terms = "La membresía es personal e intransferible.";
  private static readonly DateOnly Today = DateOnly.FromDateTime(BogotaClock.ToLocal(DateTime.UtcNow));

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public CustomerMembershipsHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task Customers_ReadOnlyTheirOwnMemberships_WithoutAdminData()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var adminId = await SeedAdminUserAsync(context.Factory);
    var ana = await RegisterCustomerAsync(context.Client, "ana");
    var beto = await RegisterCustomerAsync(context.Client, "beto");
    var sinPerfil = await RegisterCustomerAsync(context.Client, "sinperfil");

    Guid active;
    Guid annulled;
    Guid foreign;
    Guid incomeId;
    using (var scope = context.Factory.Services.CreateScope())
    {
      var services = scope.ServiceProvider;
      var customers = services.GetRequiredService<IAdminCustomersService>();
      var anaProfile = await customers.CreateAsync(adminId, new CustomerCreateRequest("Ana Gómez", "3001112233", $"{Guid.NewGuid():N}@example.com"));
      await customers.LinkAccountAsync(adminId, anaProfile.Id, new CustomerLinkAccountRequest(ana.UserId, true));
      var betoProfile = await customers.CreateAsync(adminId, new CustomerCreateRequest("Beto Ruiz", "3004445566", $"{Guid.NewGuid():N}@example.com"));
      await customers.LinkAccountAsync(adminId, betoProfile.Id, new CustomerLinkAccountRequest(beto.UserId, true));

      var haircut = await services.GetRequiredService<IAdminServicesService>()
          .CreateAsync(new ServiceCreateRequest($"Corte {Guid.NewGuid():N}"[..20], 25000, 30));
      var plan = await services.GetRequiredService<IAdminMembershipPlansService>().CreateAsync(new MembershipPlanUpsertRequest(
          $"Oro {Guid.NewGuid():N}"[..20], "Diez cortes", 250000, "Pool", 10, 90, Terms, null, [new(haircut.Id, null)],
          [new(null, "Service", haircut.Id, null, 1, "EveryPurchase"), new(null, "Physical", null, "Gorra", 1, "EveryPurchase")]));

      var memberships = services.GetRequiredService<IAdminMembershipsService>();
      active = (await memberships.SellAsync(adminId, anaProfile.Id, new MembershipSaleRequest(plan.Id, Today, ActivateNow: true))).Id;
      annulled = (await memberships.SellAsync(adminId, anaProfile.Id, new MembershipSaleRequest(plan.Id, Today, ActivateNow: true))).Id;
      foreign = (await memberships.SellAsync(adminId, betoProfile.Id, new MembershipSaleRequest(plan.Id, Today, ActivateNow: true))).Id;

      var ledger = services.GetRequiredService<IAdminMembershipLedgerService>();
      var consumed = await ledger.ConsumeAsync(adminId, active, new MembershipConsumptionRequest(haircut.Id, 3, Today, null, Guid.NewGuid()));
      await ledger.ReverseAsync(adminId, active, consumed.Consumptions.Single().Id, new MembershipConsumptionReverseRequest("Motivo secreto de reverso"));
      await ledger.ConsumeAsync(adminId, active, new MembershipConsumptionRequest(haircut.Id, 2, Today, null, Guid.NewGuid()));
      await memberships.AnnulAsync(adminId, annulled, new MembershipAnnulRequest("Motivo secreto de anulación"));

      var db = services.GetRequiredService<AppDbContext>();
      var income = IncomeEntry.CreateMembershipSale(250000, Today, adminId, DateTime.UtcNow);
      db.IncomeEntries.Add(income);
      await db.SaveChangesAsync();
      incomeId = income.Id;
      await memberships.LinkIncomeAsync(adminId, active, new MembershipIncomeLinkRequest(incomeId));
    }

    // Ana sees her two memberships and nothing of Beto's.
    AuthenticateAs(context.Client, ana.AccessToken);
    var list = (await context.Client.GetFromJsonAsync<List<MyMembershipSummaryView>>("/api/v1/customer/memberships"))!;
    Assert.Equal(new[] { active, annulled }.Order(), list.Select(summary => summary.Id).Order());
    Assert.Equal("Annulled", list.Single(summary => summary.Id == annulled).DisplayStatus);
    var summaryOfActive = list.Single(summary => summary.Id == active);
    Assert.Equal((10, 9, 1, 0), (summaryOfActive.PurchasedGranted, summaryOfActive.PurchasedAvailable, summaryOfActive.GiftedGranted, summaryOfActive.GiftedAvailable));

    using var detail = await context.Client.GetAsync($"/api/v1/customer/memberships/{active}");
    Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
    var json = await detail.Content.ReadAsStringAsync();
    var view = JsonSerializer.Deserialize<MyMembershipView>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    Assert.Equal(2, view.Consumptions.Count);
    Assert.Single(view.Consumptions, consumption => consumption.IsReversed);

    // No admin-only data: income, history, reasons, admins, customer ids or other customers.
    foreach (var forbidden in new[]
             {
               "income", "events", "reason", "registeredByName", "performedByName", "activatedByName", "annulledByName",
               "customerId", "allowanceId", "giftId", "staff",
               "Motivo secreto", "Admin Semilla", "Beto", incomeId.ToString(),
             })
    {
      Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
    }

    using var annulledDetail = await context.Client.GetAsync($"/api/v1/customer/memberships/{annulled}");
    var annulledJson = await annulledDetail.Content.ReadAsStringAsync();
    Assert.DoesNotContain("Motivo secreto", annulledJson, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("annulledBy", annulledJson, StringComparison.OrdinalIgnoreCase);

    // Beto's membership answers as if it did not exist.
    using var foreignDetail = await context.Client.GetAsync($"/api/v1/customer/memberships/{foreign}");
    Assert.Equal(HttpStatusCode.NotFound, foreignDetail.StatusCode);

    // An account without a linked profile gets an empty list and no detail.
    AuthenticateAs(context.Client, sinPerfil.AccessToken);
    Assert.Empty((await context.Client.GetFromJsonAsync<List<MyMembershipSummaryView>>("/api/v1/customer/memberships"))!);
    using var noProfileDetail = await context.Client.GetAsync($"/api/v1/customer/memberships/{active}");
    Assert.Equal(HttpStatusCode.NotFound, noProfileDetail.StatusCode);

    // Without a session nothing is returned.
    context.Client.DefaultRequestHeaders.Authorization = null;
    using var anonymousList = await context.Client.GetAsync("/api/v1/customer/memberships");
    Assert.Equal(HttpStatusCode.Unauthorized, anonymousList.StatusCode);
    using var anonymousDetail = await context.Client.GetAsync($"/api/v1/customer/memberships/{active}");
    Assert.Equal(HttpStatusCode.Unauthorized, anonymousDetail.StatusCode);
  }

  [Fact]
  public async Task UnlinkingTheAccount_CutsAccessAtOnce_AndRelinkingShowsOnlyTheNewProfile()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var adminId = await SeedAdminUserAsync(context.Factory);
    var account = await RegisterCustomerAsync(context.Client, "relink");
    Guid profileA;
    Guid profileB;
    Guid membershipA;
    Guid membershipB;
    using (var scope = context.Factory.Services.CreateScope())
    {
      var services = scope.ServiceProvider;
      var customers = services.GetRequiredService<IAdminCustomersService>();
      profileA = (await customers.CreateAsync(adminId, new CustomerCreateRequest("Cliente A", "3001112233", $"{Guid.NewGuid():N}@example.com"))).Id;
      profileB = (await customers.CreateAsync(adminId, new CustomerCreateRequest("Cliente B", "3004445566", $"{Guid.NewGuid():N}@example.com"))).Id;
      await customers.LinkAccountAsync(adminId, profileA, new CustomerLinkAccountRequest(account.UserId, true));

      var haircut = await services.GetRequiredService<IAdminServicesService>()
          .CreateAsync(new ServiceCreateRequest($"Corte {Guid.NewGuid():N}"[..20], 25000, 30));
      var plan = await services.GetRequiredService<IAdminMembershipPlansService>().CreateAsync(new MembershipPlanUpsertRequest(
          $"Oro {Guid.NewGuid():N}"[..20], null, 250000, "Pool", 10, 90, Terms, null, [new(haircut.Id, null)], []));
      var memberships = services.GetRequiredService<IAdminMembershipsService>();
      membershipA = (await memberships.SellAsync(adminId, profileA, new MembershipSaleRequest(plan.Id, Today, ActivateNow: true))).Id;
      membershipB = (await memberships.SellAsync(adminId, profileB, new MembershipSaleRequest(plan.Id, Today, ActivateNow: true))).Id;
    }

    // The same session (token) is used throughout: access follows the current link, not the token.
    AuthenticateAs(context.Client, account.AccessToken);
    Assert.Equal([membershipA], (await ListMineAsync(context.Client)).Select(summary => summary.Id));
    using (var detail = await context.Client.GetAsync($"/api/v1/customer/memberships/{membershipA}"))
    {
      Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
    }

    using (var scope = context.Factory.Services.CreateScope())
    {
      await scope.ServiceProvider.GetRequiredService<IAdminCustomersService>()
          .UnlinkAccountAsync(adminId, profileA, new CustomerUnlinkAccountRequest("La cuenta no era de este cliente"));
    }

    Assert.Empty(await ListMineAsync(context.Client));
    using (var afterUnlink = await context.Client.GetAsync($"/api/v1/customer/memberships/{membershipA}"))
    {
      Assert.Equal(HttpStatusCode.NotFound, afterUnlink.StatusCode);
    }

    using (var scope = context.Factory.Services.CreateScope())
    {
      await scope.ServiceProvider.GetRequiredService<IAdminCustomersService>()
          .LinkAccountAsync(adminId, profileB, new CustomerLinkAccountRequest(account.UserId, true));
    }

    Assert.Equal([membershipB], (await ListMineAsync(context.Client)).Select(summary => summary.Id));
    using (var ownDetail = await context.Client.GetAsync($"/api/v1/customer/memberships/{membershipB}"))
    {
      Assert.Equal(HttpStatusCode.OK, ownDetail.StatusCode);
    }

    using (var previousDetail = await context.Client.GetAsync($"/api/v1/customer/memberships/{membershipA}"))
    {
      Assert.Equal(HttpStatusCode.NotFound, previousDetail.StatusCode);
    }
  }

  private static async Task<List<MyMembershipSummaryView>> ListMineAsync(HttpClient client)
      => (await client.GetFromJsonAsync<List<MyMembershipSummaryView>>("/api/v1/customer/memberships"))!;

  [Fact]
  public async Task CustomerEndpoints_AreReadOnly()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var customer = await RegisterCustomerAsync(context.Client, "solo-lectura");
    AuthenticateAs(context.Client, customer.AccessToken);
    var someId = Guid.NewGuid();

    using var post = await context.Client.PostAsJsonAsync("/api/v1/customer/memberships", new { });
    using var put = await context.Client.PutAsJsonAsync($"/api/v1/customer/memberships/{someId}", new { });
    using var delete = await context.Client.DeleteAsync($"/api/v1/customer/memberships/{someId}");
    using var consume = await context.Client.PostAsJsonAsync($"/api/v1/customer/memberships/{someId}/consumptions", new { });
    Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    Assert.Equal(HttpStatusCode.MethodNotAllowed, put.StatusCode);
    Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, consume.StatusCode);

    // And the admin routes stay closed to customers.
    using var adminDetail = await context.Client.GetAsync($"/api/v1/admin/memberships/{someId}");
    Assert.Equal(HttpStatusCode.Forbidden, adminDetail.StatusCode);
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
