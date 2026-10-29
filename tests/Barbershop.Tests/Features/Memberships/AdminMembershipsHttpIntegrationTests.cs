using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Finance.Admin;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Application.Services.Admin;
using Barbershop.Domain.Common;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Memberships;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Memberships;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "AdminMembershipsHttpIntegration")]
public sealed class AdminMembershipsHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";
  private const string Terms = "La membresía es personal e intransferible.";
  private static readonly DateOnly Today = DateOnly.FromDateTime(BogotaClock.ToLocal(DateTime.UtcNow));

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public AdminMembershipsHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task Admin_SellsActivatesLinksAndAnnulsAMembership()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAndPromoteAdminAsync(context, "sales");
    AuthenticateAs(context.Client, admin.AccessToken);
    var (customerId, planId) = await SeedCustomerAndPlanAsync(context.Factory, admin.UserId,
        [new(null, "Physical", null, "Gorra", 1, "FirstPurchaseOfPlan")]);

    using var sold = await context.Client.PostAsJsonAsync(
        $"/api/v1/admin/customers/{customerId}/memberships", new MembershipSaleRequest(planId, Today));
    Assert.Equal(HttpStatusCode.Created, sold.StatusCode);
    var membership = (await sold.Content.ReadFromJsonAsync<CustomerMembershipDetailView>())!;
    Assert.Equal("PendingActivation", membership.Status);
    Assert.Equal(Today.AddDays(89), membership.EndsOn);
    Assert.Equal("PendingDelivery", Assert.Single(membership.Gifts).Status);

    var list = await context.Client.GetFromJsonAsync<List<CustomerMembershipSummaryView>>($"/api/v1/admin/customers/{customerId}/memberships");
    Assert.Equal(membership.Id, Assert.Single(list!).Id);

    using var activated = await context.Client.PostAsJsonAsync(
        $"/api/v1/admin/memberships/{membership.Id}/activate", new MembershipActivateRequest());
    Assert.Equal(HttpStatusCode.OK, activated.StatusCode);

    // The sale is recorded by hand in finance, then linked.
    using var incomeResponse = await context.Client.PostAsJsonAsync("/api/v1/admin/income",
        new IncomeEntryCreateRequest(null, null, 250000, false, Today, "MembershipSale"));
    var income = (await incomeResponse.Content.ReadFromJsonAsync<IncomeEntryView>())!;
    var candidates = await context.Client.GetFromJsonAsync<List<MembershipIncomeCandidateView>>(
        $"/api/v1/admin/memberships/income-candidates?date={Today:yyyy-MM-dd}");
    Assert.Equal(income.Id, Assert.Single(candidates!).IncomeEntryId);

    using var linked = await context.Client.PutAsJsonAsync(
        $"/api/v1/admin/memberships/{membership.Id}/income-link", new MembershipIncomeLinkRequest(income.Id));
    Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
    Assert.False((await linked.Content.ReadFromJsonAsync<CustomerMembershipDetailView>())!.Income!.HasDiscrepancy);

    using var deleteIncome = await context.Client.DeleteAsync($"/api/v1/admin/income/{income.Id}");
    Assert.Equal(HttpStatusCode.Conflict, deleteIncome.StatusCode);

    using var annulled = await context.Client.PostAsJsonAsync(
        $"/api/v1/admin/memberships/{membership.Id}/annul", new MembershipAnnulRequest("Retracto dentro del plazo legal"));
    Assert.Equal(HttpStatusCode.OK, annulled.StatusCode);
    var final = (await annulled.Content.ReadFromJsonAsync<CustomerMembershipDetailView>())!;
    Assert.Equal("Annulled", final.Status);
    Assert.Equal("NotDeliverable", Assert.Single(final.Gifts).Status);
    Assert.NotNull(final.Income); // annulling refunds nothing and keeps the income link

    // No income or commission was generated: only the one income registered by hand exists.
    using var scope = context.Factory.Services.CreateScope();
    Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().IncomeEntries.CountAsync());
  }

  [Fact]
  public async Task MembershipEndpoints_AreAdminOnly()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    using (var anonymous = await context.Client.GetAsync($"/api/v1/admin/memberships/{Guid.NewGuid()}"))
    {
      Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    var customer = await RegisterCustomerAsync(context.Client, "memberships-forbidden");
    AuthenticateAs(context.Client, customer.AccessToken);

    using var sell = await context.Client.PostAsJsonAsync(
        $"/api/v1/admin/customers/{Guid.NewGuid()}/memberships", new MembershipSaleRequest(Guid.NewGuid(), Today));
    Assert.Equal(HttpStatusCode.Forbidden, sell.StatusCode);

    using var detail = await context.Client.GetAsync($"/api/v1/admin/memberships/{Guid.NewGuid()}");
    Assert.Equal(HttpStatusCode.Forbidden, detail.StatusCode);

    using var annul = await context.Client.PostAsJsonAsync(
        $"/api/v1/admin/memberships/{Guid.NewGuid()}/annul", new MembershipAnnulRequest("Intento"));
    Assert.Equal(HttpStatusCode.Forbidden, annul.StatusCode);
  }

  [Fact]
  public async Task ConcurrentSales_GrantTheFirstPurchaseGiftExactlyOnce()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var adminId = await SeedAdminUserAsync(context.Factory);
    var (customerId, planId) = await SeedCustomerAndPlanAsync(context.Factory, adminId,
        [new(null, "Physical", null, "Gorra", 1, "FirstPurchaseOfPlan")]);

    var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => TrySellAsync(context.Factory, adminId, customerId, planId)));

    Assert.True(outcomes.Count(succeeded => succeeded) >= 1);
    using var scope = context.Factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var memberships = await db.CustomerMemberships.CountAsync(membership => membership.CustomerId == customerId);
    Assert.Equal(outcomes.Count(succeeded => succeeded), memberships);
    Assert.Equal(1, await db.MembershipGifts.CountAsync(gift => gift.Condition == MembershipGiftCondition.FirstPurchaseOfPlan));
  }

  [Fact]
  public async Task ConcurrentActivations_RecordASingleActivation()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var adminId = await SeedAdminUserAsync(context.Factory);
    var (customerId, planId) = await SeedCustomerAndPlanAsync(context.Factory, adminId, []);
    Guid membershipId;
    using (var scope = context.Factory.Services.CreateScope())
    {
      var service = scope.ServiceProvider.GetRequiredService<IAdminMembershipsService>();
      membershipId = (await service.SellAsync(adminId, customerId, new MembershipSaleRequest(planId, Today))).Id;
    }

    var outcomes = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
    {
      using var scope = context.Factory.Services.CreateScope();
      try
      {
        await scope.ServiceProvider.GetRequiredService<IAdminMembershipsService>()
            .ActivateAsync(adminId, membershipId, new MembershipActivateRequest());
        return true;
      }
      catch (ConflictException)
      {
        return false;
      }
    }));

    Assert.Equal(1, outcomes.Count(succeeded => succeeded));
    using var check = context.Factory.Services.CreateScope();
    var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
    Assert.Equal(1, await db.MembershipEvents.CountAsync(membershipEvent =>
        membershipEvent.CustomerMembershipId == membershipId && membershipEvent.Type == MembershipEventType.Activated));
  }

  [Fact]
  public async Task ConcurrentLinksToOneIncome_AllLand_AndTheTotalAddsUp()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var adminId = await SeedAdminUserAsync(context.Factory);
    var (customerId, planId) = await SeedCustomerAndPlanAsync(context.Factory, adminId, []);
    var membershipIds = new List<Guid>();
    Guid incomeId;
    using (var scope = context.Factory.Services.CreateScope())
    {
      var service = scope.ServiceProvider.GetRequiredService<IAdminMembershipsService>();
      for (var i = 0; i < 3; i++)
      {
        membershipIds.Add((await service.SellAsync(adminId, customerId, new MembershipSaleRequest(planId, Today))).Id);
      }

      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var income = IncomeEntry.CreateMembershipSale(750000, Today, adminId, DateTime.UtcNow);
      db.IncomeEntries.Add(income);
      await db.SaveChangesAsync();
      incomeId = income.Id;
    }

    // Linked one by one at the same time: each needs a justification until all three are on the income.
    await Task.WhenAll(membershipIds.Select(async membershipId =>
    {
      using var scope = context.Factory.Services.CreateScope();
      await scope.ServiceProvider.GetRequiredService<IAdminMembershipsService>().LinkIncomeAsync(
          adminId, membershipId, new MembershipIncomeLinkRequest(incomeId, DiscrepancyJustification: "Pago conjunto, se vinculan por separado"));
    }));

    using var check = context.Factory.Services.CreateScope();
    var memberships = check.ServiceProvider.GetRequiredService<IAdminMembershipsService>();
    var detail = await memberships.GetByIdAsync(membershipIds[0]);
    Assert.Equal(3, detail.Income!.LinkedMemberships.Count);
    Assert.Equal(750000, detail.Income.LinkedMembershipsTotal);
    Assert.False(detail.Income.HasDiscrepancy);
  }

  [Fact]
  public async Task DatabaseConstraints_ProtectDatesAuditAndGiftUniqueness()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var adminId = await SeedAdminUserAsync(context.Factory);
    var (customerId, planId) = await SeedCustomerAndPlanAsync(context.Factory, adminId,
        [new(null, "Physical", null, "Gorra", 1, "EveryPurchase")]);
    Guid membershipId;
    using (var scope = context.Factory.Services.CreateScope())
    {
      membershipId = (await scope.ServiceProvider.GetRequiredService<IAdminMembershipsService>()
          .SellAsync(adminId, customerId, new MembershipSaleRequest(planId, Today))).Id;
    }

    using var sqlScope = context.Factory.Services.CreateScope();
    var db = sqlScope.ServiceProvider.GetRequiredService<AppDbContext>();
    var gift = await db.MembershipGifts.SingleAsync();

    await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
        $"UPDATE public.customer_memberships SET \"EndsOn\" = \"StartsOn\" - 1 WHERE \"Id\" = {membershipId}"));
    await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
        $"UPDATE public.customer_memberships SET \"Status\" = 'Annulled' WHERE \"Id\" = {membershipId}"));
    await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
        $"UPDATE public.customer_memberships SET \"Status\" = 'Active' WHERE \"Id\" = {membershipId}"));
    await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.membership_gifts
          ("Id", "CustomerMembershipId", "MembershipPlanGiftRuleId", "Kind", "Condition", "Quantity", "Position", "DescriptionSnapshot", "CreatedAt")
        VALUES ({Guid.NewGuid()}, {membershipId}, {gift.MembershipPlanGiftRuleId}, 'Physical', 'EveryPurchase', 1, 0, 'Gorra', now())
        """));
  }

  [Fact]
  public async Task OnPostgres_IncomeLinksLeaveTheMembershipAndTheIncomeIntact_AndSoldGiftsSurvivePlanChanges()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var adminId = await SeedAdminUserAsync(context.Factory);
    var (customerId, planId) = await SeedCustomerAndPlanAsync(context.Factory, adminId,
    [
      new(null, "Physical", null, "Gorra", 1, "FirstPurchaseOfPlan"),
    ]);
    Guid membershipId;
    Guid incomeId;
    string before;
    using (var scope = context.Factory.Services.CreateScope())
    {
      var plans = scope.ServiceProvider.GetRequiredService<IAdminMembershipPlansService>();
      var plan = await plans.GetByIdAsync(planId);
      // Add a service gift too, so both kinds are sold.
      await plans.UpdateAsync(planId, new MembershipPlanUpsertRequest(
          plan.Name, null, 250000, "Pool", 10, 90, Terms, null, [new(plan.Services[0].ServiceId, null)],
          [
            new(plan.GiftRules[0].Id, "Physical", null, "Gorra", 1, "FirstPurchaseOfPlan"),
            new(null, "Service", plan.Services[0].ServiceId, null, 2, "EveryPurchase"),
          ]));

      var memberships = scope.ServiceProvider.GetRequiredService<IAdminMembershipsService>();
      membershipId = (await memberships.SellAsync(adminId, customerId, new MembershipSaleRequest(planId, Today, ActivateNow: true))).Id;
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var income = IncomeEntry.CreateMembershipSale(260000, Today, adminId, DateTime.UtcNow);
      db.IncomeEntries.Add(income);
      await db.SaveChangesAsync();
      incomeId = income.Id;
      before = await PersistedFingerprintAsync(context.Factory, membershipId, incomeId);
    }

    using (var scope = context.Factory.Services.CreateScope())
    {
      var memberships = scope.ServiceProvider.GetRequiredService<IAdminMembershipsService>();
      await memberships.LinkIncomeAsync(adminId, membershipId,
          new MembershipIncomeLinkRequest(incomeId, DiscrepancyJustification: "Incluye 10.000 de domicilio"));
    }

    Assert.Equal(before, await PersistedFingerprintAsync(context.Factory, membershipId, incomeId));

    using (var scope = context.Factory.Services.CreateScope())
    {
      var incomes = scope.ServiceProvider.GetRequiredService<IAdminIncomeService>();
      await Assert.ThrowsAsync<ConflictException>(() => incomes.SoftDeleteAsync(incomeId));
    }

    // Edit the plan dropping both gift rules, then deactivate it: the sold gifts must stay.
    using (var scope = context.Factory.Services.CreateScope())
    {
      var plans = scope.ServiceProvider.GetRequiredService<IAdminMembershipPlansService>();
      var plan = await plans.GetByIdAsync(planId);
      await plans.UpdateAsync(planId, new MembershipPlanUpsertRequest(
          "Plan renombrado", null, 300000, "Pool", 5, 30, "Términos nuevos", null,
          [new(plan.Services[0].ServiceId, null)], [new(null, "Physical", null, "Champú", 1, "EveryPurchase")]));
      await plans.UpdateStatusAsync(planId, new MembershipPlanStatusUpdateRequest(false));
    }

    Assert.Equal(before, await PersistedFingerprintAsync(context.Factory, membershipId, incomeId));

    using (var scope = context.Factory.Services.CreateScope())
    {
      var memberships = scope.ServiceProvider.GetRequiredService<IAdminMembershipsService>();
      var unlinked = await memberships.UnlinkIncomeAsync(adminId, membershipId, new MembershipIncomeUnlinkRequest());
      Assert.Null(unlinked.Income);
      Assert.Equal("Incluye 10.000 de domicilio", unlinked.Events.Single(entry => entry.Type == "IncomeLinked").Reason);
      Assert.Contains(unlinked.Events, entry => entry.Type == "IncomeUnlinked" && entry.IncomeEntryId == incomeId);
    }

    Assert.Equal(before, await PersistedFingerprintAsync(context.Factory, membershipId, incomeId));

    using (var scope = context.Factory.Services.CreateScope())
    {
      await scope.ServiceProvider.GetRequiredService<IAdminIncomeService>().SoftDeleteAsync(incomeId);
    }
  }

  /// <summary>
  /// Raw persisted state of a membership (price, status, balances, gifts) and of an income (kind, amount,
  /// deletion, last update) plus the row counts, read with a fresh context so nothing comes from tracking.
  /// </summary>
  private static async Task<string> PersistedFingerprintAsync(IntegrationTestFactory factory, Guid membershipId, Guid incomeId)
  {
    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var membership = await db.CustomerMemberships.AsNoTracking().SingleAsync(entry => entry.Id == membershipId);
    var allowances = await db.MembershipAllowances.AsNoTracking()
        .Where(line => line.CustomerMembershipId == membershipId)
        .OrderBy(line => line.Position)
        .Select(line => $"{line.Id}|{line.Origin}|{line.MembershipGiftId}|{line.GrantedQuantity}")
        .ToListAsync();
    var allowanceServices = await db.MembershipAllowanceServices.AsNoTracking()
        .Where(line => db.MembershipAllowances.Any(allowance =>
            allowance.Id == line.MembershipAllowanceId && allowance.CustomerMembershipId == membershipId))
        .OrderBy(line => line.ServiceNameSnapshot)
        .Select(line => $"{line.MembershipAllowanceId}|{line.ServiceNameSnapshot}")
        .ToListAsync();
    var gifts = await db.MembershipGifts.AsNoTracking()
        .Where(gift => gift.CustomerMembershipId == membershipId)
        .OrderBy(gift => gift.Position)
        .Select(gift => $"{gift.Id}|{gift.Kind}|{gift.Condition}|{gift.Quantity}|{gift.ServiceNameSnapshot}|{gift.DescriptionSnapshot}")
        .ToListAsync();
    var income = await db.IncomeEntries.AsNoTracking().SingleAsync(entry => entry.Id == incomeId);
    var incomeCount = await db.IncomeEntries.CountAsync();

    return string.Join(" / ",
    [
      $"{membership.PlanName}|{membership.Price}|{membership.Mode}|{membership.Terms}|{membership.Status}|{membership.ActivatedAt:O}|{membership.StartsOn}|{membership.EndsOn}",
      .. allowances,
      .. allowanceServices,
      .. gifts,
      $"{incomeCount}|{income.Kind}|{income.Amount}|{income.OccurredOn}|{income.IsDeleted}|{income.UpdatedAt:O}",
    ]);
  }

  private static async Task<bool> TrySellAsync(IntegrationTestFactory factory, Guid adminId, Guid customerId, Guid planId)
  {
    using var scope = factory.Services.CreateScope();
    try
    {
      await scope.ServiceProvider.GetRequiredService<IAdminMembershipsService>()
          .SellAsync(adminId, customerId, new MembershipSaleRequest(planId, Today));
      return true;
    }
    catch (ConflictException)
    {
      return false;
    }
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

  private static async Task<(Guid CustomerId, Guid PlanId)> SeedCustomerAndPlanAsync(
      IntegrationTestFactory factory,
      Guid adminId,
      IReadOnlyList<MembershipPlanGiftRuleRequest> gifts)
  {
    using var scope = factory.Services.CreateScope();
    var services = scope.ServiceProvider.GetRequiredService<IAdminServicesService>();
    var haircut = await services.CreateAsync(new ServiceCreateRequest($"Corte {Guid.NewGuid():N}"[..20], 25000, 30));
    var customers = scope.ServiceProvider.GetRequiredService<IAdminCustomersService>();
    var customer = await customers.CreateAsync(adminId, new CustomerCreateRequest("Carlos Pérez", "3001234567", $"{Guid.NewGuid():N}@example.com"));
    var plans = scope.ServiceProvider.GetRequiredService<IAdminMembershipPlansService>();
    var plan = await plans.CreateAsync(new MembershipPlanUpsertRequest(
        $"Oro {Guid.NewGuid():N}"[..20], null, 250000, "Pool", 10, 90, Terms, null, [new(haircut.Id, null)], gifts));
    return (customer.Id, plan.Id);
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
