using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Finance.Admin;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Domain.Common;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Services;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Customers;
using Barbershop.Infrastructure.Finance;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Barbershop.Tests.Features.Memberships;

public sealed class MembershipSalesServiceTests : IDisposable
{
  private const string Terms = "La membresía es personal e intransferible.";

  private readonly DateOnly _today = DateOnly.FromDateTime(BogotaClock.ToLocal(DateTime.UtcNow));
  private readonly AppDbContext _dbContext;
  private readonly IAdminMembershipsService _service;
  private readonly IAdminMembershipPlansService _plans;
  private readonly IAdminCustomersService _customers;
  private readonly Guid _adminId;
  private readonly Service _haircut;
  private readonly Service _beard;

  public MembershipSalesServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        // The in-memory provider has no transactions; income linking and merges open one for PostgreSQL.
        .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options;

    _dbContext = new AppDbContext(options);
    _service = new MembershipSalesService(_dbContext, TimeProvider.System);
    _plans = new MembershipPlanManagementService(_dbContext, TimeProvider.System);
    _customers = new CustomerManagementService(_dbContext, TimeProvider.System);

    var admin = new User("Admin Principal", "admin@example.com", "hash", DateTime.UtcNow);
    _haircut = new Service("Corte", 25000, DateTime.UtcNow, 30);
    _beard = new Service("Barba", 15000, DateTime.UtcNow, 30);
    _dbContext.Users.Add(admin);
    _dbContext.Services.AddRange(_haircut, _beard);
    _dbContext.SaveChanges();
    _adminId = admin.Id;
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task SellAsync_CopiesThePlan_IsPendingByDefault_AndCreatesNoIncome()
  {
    var customer = await CreateCustomerAsync();
    var plan = await CreatePoolPlanAsync();

    var sold = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today));

    Assert.Equal("PendingActivation", sold.Status);
    Assert.Equal("PendingActivation", sold.DisplayStatus);
    Assert.Equal("Oro", sold.PlanName);
    Assert.Equal("Diez servicios", sold.PlanDescription);
    Assert.Equal(250000, sold.Price);
    Assert.Equal("Pool", sold.Mode);
    Assert.Equal(Terms, sold.Terms);
    Assert.Equal("Solo de lunes a jueves", sold.SpecificConditions);
    Assert.Equal(_today, sold.SoldOn);
    Assert.Equal(customer.Id, sold.OriginalCustomerId);

    var pool = Assert.Single(sold.Allowances);
    Assert.Equal("Purchased", pool.Origin);
    Assert.Equal(10, pool.GrantedQuantity);
    Assert.Equal(10, pool.AvailableQuantity);
    Assert.Equal(["Barba", "Corte"], pool.Services.Select(service => service.ServiceName));

    Assert.Equal("Sold", Assert.Single(sold.Events).Type);
    Assert.Empty(await _dbContext.IncomeEntries.ToListAsync());
  }

  [Fact]
  public async Task SellAsync_PerServicePlan_CopiesOneBalanceLinePerService()
  {
    var customer = await CreateCustomerAsync();
    var plan = await _plans.CreateAsync(new MembershipPlanUpsertRequest(
        "Plata", null, 180000, "PerService", null, 60, Terms, null,
        [new(_haircut.Id, 8), new(_beard.Id, 4)], []));

    var sold = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today));

    Assert.Equal(2, sold.Allowances.Count);
    Assert.Equal(8, sold.Allowances.Single(line => line.Services.Single().ServiceName == "Corte").GrantedQuantity);
    Assert.Equal(4, sold.Allowances.Single(line => line.Services.Single().ServiceName == "Barba").GrantedQuantity);
  }

  [Fact]
  public async Task SellAsync_DefaultsTheInclusiveEndDate_AndAcceptsAnAdjustedOne()
  {
    var customer = await CreateCustomerAsync();
    var plan = await CreatePoolPlanAsync(durationDays: 30);
    var start = _today.AddDays(5);

    var byDefault = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, start));
    Assert.Equal(start.AddDays(29), byDefault.EndsOn); // 30 days counting the first and the last

    var adjusted = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, start, start.AddDays(44)));
    Assert.Equal(start.AddDays(44), adjusted.EndsOn);

    var oneDay = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, start, start));
    Assert.Equal(start, oneDay.EndsOn);
  }

  [Fact]
  public async Task SellAsync_ValidatesDatesAndPlan()
  {
    var customer = await CreateCustomerAsync();
    var plan = await CreatePoolPlanAsync();

    var badDates = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today, _today.AddDays(-1), SoldOn: _today.AddDays(1))));
    Assert.Contains("endsOn", badDates.Errors.Keys);
    Assert.Contains("soldOn", badDates.Errors.Keys);

    await _plans.UpdateStatusAsync(plan.Id, new MembershipPlanStatusUpdateRequest(false));
    var inactive = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today)));
    Assert.Contains("planId", inactive.Errors.Keys);

    await Assert.ThrowsAsync<NotFoundException>(() =>
        _service.SellAsync(_adminId, Guid.NewGuid(), new MembershipSaleRequest(plan.Id, _today)));
  }

  [Fact]
  public async Task SellAsync_RejectsAMergedCustomer()
  {
    var target = await CreateCustomerAsync("Destino");
    var source = await CreateCustomerAsync("Origen");
    var plan = await CreatePoolPlanAsync();
    await _customers.MergeAsync(_adminId, target.Id, new CustomerMergeRequest(source.Id));

    await Assert.ThrowsAsync<ConflictException>(() =>
        _service.SellAsync(_adminId, source.Id, new MembershipSaleRequest(plan.Id, _today)));
  }

  [Fact]
  public async Task Activation_CanHappenAtSaleOrLater_ButOnlyOnce()
  {
    var customer = await CreateCustomerAsync();
    var plan = await CreatePoolPlanAsync();

    var activeNow = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today, ActivateNow: true));
    Assert.Equal("Active", activeNow.Status);
    Assert.Equal("Active", activeNow.DisplayStatus);
    Assert.Equal("Admin Principal", activeNow.ActivatedByName);
    Assert.Contains(activeNow.Events, membershipEvent => membershipEvent.Type == "Activated");

    var later = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today.AddDays(10)));
    var activated = await _service.ActivateAsync(_adminId, later.Id, new MembershipActivateRequest());
    Assert.Equal("Active", activated.Status);
    Assert.Equal("Scheduled", activated.DisplayStatus); // active, but its validity starts in 10 days

    await Assert.ThrowsAsync<ConflictException>(() => _service.ActivateAsync(_adminId, later.Id, new MembershipActivateRequest()));
  }

  [Fact]
  public async Task ActivatingAnExpiredMembership_IsExceptional_AndNeedsConfirmationAndReason()
  {
    var customer = await CreateCustomerAsync();
    var plan = await CreatePoolPlanAsync();
    var start = _today.AddDays(-40);
    var end = _today.AddDays(-10);

    var atSale = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, start, end, ActivateNow: true)));
    Assert.Contains("confirmExpiredActivation", atSale.Errors.Keys);
    Assert.Contains("activationReason", atSale.Errors.Keys);

    var pending = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, start, end));
    Assert.Equal("PendingActivation", pending.DisplayStatus);

    var withoutReason = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.ActivateAsync(_adminId, pending.Id, new MembershipActivateRequest(ConfirmExpiredActivation: true)));
    Assert.Contains("reason", withoutReason.Errors.Keys);

    var activated = await _service.ActivateAsync(_adminId, pending.Id,
        new MembershipActivateRequest(ConfirmExpiredActivation: true, Reason: "Venta registrada tarde; servicios ya prestados"));
    Assert.Equal("Active", activated.Status);
    Assert.Equal("Expired", activated.DisplayStatus);
    var audit = Assert.Single(activated.Events, membershipEvent => membershipEvent.Type == "ActivatedAfterExpiry");
    Assert.Equal("Venta registrada tarde; servicios ya prestados", audit.Reason);
  }

  [Fact]
  public async Task Gifts_EveryPurchaseIsAlwaysGranted_FirstPurchaseOnlyOncePerPlan()
  {
    var customer = await CreateCustomerAsync();
    var plan = await CreatePoolPlanAsync(gifts:
    [
      new(null, "Service", _haircut.Id, null, 1, "FirstPurchaseOfPlan"),
      new(null, "Physical", null, "Cera para cabello", 2, "EveryPurchase"),
    ]);
    var otherPlan = await CreatePoolPlanAsync(name: "Bronce", gifts: [new(null, "Physical", null, "Gorra", 1, "FirstPurchaseOfPlan")]);

    var first = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today));
    var second = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today));
    // A first purchase of another plan still gets that plan's first-purchase gift.
    var otherFirst = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(otherPlan.Id, _today));

    Assert.Equal(["Service", "Physical"], first.Gifts.Select(gift => gift.Kind));
    var serviceGift = first.Gifts[0];
    Assert.Equal("Corte", serviceGift.ServiceName);
    Assert.Equal("Granted", serviceGift.Status);
    var giftLine = Assert.Single(first.Allowances, line => line.Origin == "Gift");
    Assert.Equal(serviceGift.Id, giftLine.GiftId);
    Assert.Equal(1, giftLine.GrantedQuantity);
    Assert.Equal("Corte", Assert.Single(giftLine.Services).ServiceName);
    Assert.Equal("PendingDelivery", first.Gifts[1].Status);
    Assert.Equal("Cera para cabello", first.Gifts[1].Description);
    Assert.Equal(2, first.Gifts[1].Quantity);

    Assert.Equal("Physical", Assert.Single(second.Gifts).Kind);
    Assert.DoesNotContain(second.Allowances, line => line.Origin == "Gift");
    Assert.Equal("Gorra", Assert.Single(otherFirst.Gifts).Description);
  }

  [Fact]
  public async Task Gifts_AnnulledMembershipsDoNotCountAsAFirstPurchase()
  {
    var customer = await CreateCustomerAsync();
    var plan = await CreatePoolPlanAsync(gifts: [new(null, "Physical", null, "Gorra", 1, "FirstPurchaseOfPlan")]);

    var first = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today));
    await _service.AnnulAsync(_adminId, first.Id, new MembershipAnnulRequest("Venta registrada por error"));

    var again = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today));

    Assert.Equal("Gorra", Assert.Single(again.Gifts).Description);
  }

  [Fact]
  public async Task SeveralMembershipsOfTheSamePlan_CoexistWithIndependentBalances()
  {
    var customer = await CreateCustomerAsync();
    var plan = await CreatePoolPlanAsync();

    await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today, ActivateNow: true));
    await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today, ActivateNow: true));

    var list = await _service.GetCustomerMembershipsAsync(customer.Id);
    Assert.Equal(2, list.Count);
    Assert.All(list, membership => Assert.Equal("Active", membership.DisplayStatus));
    Assert.Equal(2, await _dbContext.MembershipAllowances.CountAsync());
  }

  [Fact]
  public async Task LaterCatalogChanges_DoNotAlterASoldMembership()
  {
    var customer = await CreateCustomerAsync();
    var plan = await CreatePoolPlanAsync(gifts: [new(null, "Physical", null, "Gorra", 1, "EveryPurchase")]);
    var sold = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today));

    await _plans.UpdateAsync(plan.Id, new MembershipPlanUpsertRequest(
        "Oro renovada", "Otra descripción", 300000, "PerService", null, 15, "Términos nuevos", null,
        [new(_haircut.Id, 3)], [new(null, "Physical", null, "Champú", 1, "EveryPurchase")]));
    _haircut.Update("Corte clásico", 30000, DateTime.UtcNow, 30);
    await _dbContext.SaveChangesAsync();
    await _plans.SoftDeleteAsync(plan.Id);

    var reloaded = await _service.GetByIdAsync(sold.Id);

    Assert.Equal("Oro", reloaded.PlanName);
    Assert.Equal(250000, reloaded.Price);
    Assert.Equal("Pool", reloaded.Mode);
    Assert.Equal(Terms, reloaded.Terms);
    Assert.Equal(sold.EndsOn, reloaded.EndsOn);
    Assert.Equal(10, Assert.Single(reloaded.Allowances).GrantedQuantity);
    Assert.Equal(["Barba", "Corte"], reloaded.Allowances[0].Services.Select(service => service.ServiceName));
    Assert.Equal("Gorra", Assert.Single(reloaded.Gifts).Description);
  }

  [Fact]
  public async Task AnnulAsync_RequiresAReason_BlocksPendingGifts_AndKeepsEverything()
  {
    var customer = await CreateCustomerAsync();
    var plan = await CreatePoolPlanAsync(gifts:
    [
      new(null, "Service", _haircut.Id, null, 1, "EveryPurchase"),
      new(null, "Physical", null, "Gorra", 1, "EveryPurchase"),
    ]);
    var sold = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today, ActivateNow: true));

    var noReason = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.AnnulAsync(_adminId, sold.Id, new MembershipAnnulRequest(" ")));
    Assert.Contains("reason", noReason.Errors.Keys);

    var annulled = await _service.AnnulAsync(_adminId, sold.Id, new MembershipAnnulRequest("Cliente se retracta por ley"));

    Assert.Equal("Annulled", annulled.Status);
    Assert.Equal("Cliente se retracta por ley", annulled.AnnulmentReason);
    Assert.Equal("Admin Principal", annulled.AnnulledByName);
    Assert.Equal(["Blocked", "NotDeliverable"], annulled.Gifts.Select(gift => gift.Status));
    Assert.Equal(sold.Allowances.Count, annulled.Allowances.Count);
    Assert.Contains(annulled.Events, membershipEvent => membershipEvent.Type == "Annulled" && membershipEvent.Reason == "Cliente se retracta por ley");

    await Assert.ThrowsAsync<ConflictException>(() => _service.AnnulAsync(_adminId, sold.Id, new MembershipAnnulRequest("Otra vez")));
    await Assert.ThrowsAsync<ConflictException>(() => _service.ActivateAsync(_adminId, sold.Id, new MembershipActivateRequest()));
  }

  [Fact]
  public async Task LinkIncome_OneIncomeCanCoverSeveralMemberships_WithoutJustificationWhenTheyAddUp()
  {
    var customer = await CreateCustomerAsync();
    var plan = await CreatePoolPlanAsync();
    var first = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today));
    var second = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today));
    var income = await AddMembershipSaleIncomeAsync(500000);

    var linked = await _service.LinkIncomeAsync(_adminId, first.Id, new MembershipIncomeLinkRequest(income.Id, [second.Id]));

    Assert.Equal(income.Id, linked.Income!.IncomeEntryId);
    Assert.Equal(500000, linked.Income.LinkedMembershipsTotal);
    Assert.False(linked.Income.HasDiscrepancy);
    Assert.Equal(2, linked.Income.LinkedMemberships.Count);
    Assert.Null(Assert.Single(linked.Events, membershipEvent => membershipEvent.Type == "IncomeLinked").Reason);
    Assert.Equal(income.Id, (await _service.GetByIdAsync(second.Id)).Income!.IncomeEntryId);

    var candidates = await _service.GetIncomeCandidatesAsync(_today);
    Assert.Equal(500000, Assert.Single(candidates).LinkedMembershipsTotal);
  }

  [Fact]
  public async Task LinkIncome_RequiresAJustification_WhenTheAmountDoesNotMatch_AndKeepsIt()
  {
    var customer = await CreateCustomerAsync();
    var plan = await CreatePoolPlanAsync();
    var sold = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today));
    var income = await AddMembershipSaleIncomeAsync(230000);

    var missing = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.LinkIncomeAsync(_adminId, sold.Id, new MembershipIncomeLinkRequest(income.Id)));
    Assert.Contains("discrepancyJustification", missing.Errors.Keys);
    Assert.Null((await _service.GetByIdAsync(sold.Id)).Income);

    var linked = await _service.LinkIncomeAsync(_adminId, sold.Id,
        new MembershipIncomeLinkRequest(income.Id, DiscrepancyJustification: "Descuento de 20.000 autorizado por el dueño"));

    Assert.True(linked.Income!.HasDiscrepancy);
    Assert.Equal("Descuento de 20.000 autorizado por el dueño",
        Assert.Single(linked.Events, membershipEvent => membershipEvent.Type == "IncomeLinked").Reason);
  }

  [Fact]
  public async Task LinkIncome_RejectsServiceIncome_DeletedIncome_AndADoubleLink()
  {
    var customer = await CreateCustomerAsync();
    var plan = await CreatePoolPlanAsync();
    var sold = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today));
    var serviceIncome = new IncomeEntry(_haircut.Id, "Corte", 25000, Guid.NewGuid(), 25000, false, _today, _adminId, DateTime.UtcNow, 30);
    _dbContext.IncomeEntries.Add(serviceIncome);
    await _dbContext.SaveChangesAsync();

    var wrongKind = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.LinkIncomeAsync(_adminId, sold.Id, new MembershipIncomeLinkRequest(serviceIncome.Id)));
    Assert.Contains("incomeEntryId", wrongKind.Errors.Keys);

    var deleted = await AddMembershipSaleIncomeAsync(250000);
    deleted.MarkDeleted(DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();
    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.LinkIncomeAsync(_adminId, sold.Id, new MembershipIncomeLinkRequest(deleted.Id)));

    var income = await AddMembershipSaleIncomeAsync(250000);
    await _service.LinkIncomeAsync(_adminId, sold.Id, new MembershipIncomeLinkRequest(income.Id));
    await Assert.ThrowsAsync<ConflictException>(() =>
        _service.LinkIncomeAsync(_adminId, sold.Id, new MembershipIncomeLinkRequest(income.Id)));
  }

  [Fact]
  public async Task UnlinkIncome_KeepsBalancesAndGifts_AndAsksForAJustificationIfTheRestNoLongerAddsUp()
  {
    var customer = await CreateCustomerAsync();
    var plan = await CreatePoolPlanAsync(gifts: [new(null, "Physical", null, "Gorra", 1, "EveryPurchase")]);
    var first = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today));
    var second = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today));
    var income = await AddMembershipSaleIncomeAsync(500000);
    await _service.LinkIncomeAsync(_adminId, first.Id, new MembershipIncomeLinkRequest(income.Id, [second.Id]));

    // Leaving only the second on a 500.000 income no longer adds up.
    var missing = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.UnlinkIncomeAsync(_adminId, first.Id, new MembershipIncomeUnlinkRequest()));
    Assert.Contains("discrepancyJustification", missing.Errors.Keys);

    var unlinked = await _service.UnlinkIncomeAsync(_adminId, first.Id,
        new MembershipIncomeUnlinkRequest("La primera se pagó aparte en efectivo"));

    Assert.Null(unlinked.Income);
    Assert.Equal(Balances(first), Balances(unlinked));
    Assert.Equal(first.Gifts, unlinked.Gifts);
    Assert.Equal("La primera se pagó aparte en efectivo",
        Assert.Single(unlinked.Events, membershipEvent => membershipEvent.Type == "IncomeUnlinked").Reason);

    // The last membership can leave without a justification: nothing is left to compare.
    var last = await _service.UnlinkIncomeAsync(_adminId, second.Id, new MembershipIncomeUnlinkRequest());
    Assert.Null(last.Income);
  }

  [Fact]
  public async Task DeletingAnIncomeLinkedToMemberships_IsBlocked()
  {
    var customer = await CreateCustomerAsync();
    var plan = await CreatePoolPlanAsync();
    var sold = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today));
    var income = await AddMembershipSaleIncomeAsync(250000);
    await _service.LinkIncomeAsync(_adminId, sold.Id, new MembershipIncomeLinkRequest(income.Id));
    var incomeService = new IncomeManagementService(_dbContext, TimeProvider.System);

    await Assert.ThrowsAsync<ConflictException>(() => incomeService.SoftDeleteAsync(income.Id));

    await _service.UnlinkIncomeAsync(_adminId, sold.Id, new MembershipIncomeUnlinkRequest());
    await incomeService.SoftDeleteAsync(income.Id);
  }

  [Fact]
  public async Task LinkingAndUnlinkingIncome_OnlyMovesTheLink_NeverPricesBalancesGiftsStatusOrTheIncome()
  {
    var customer = await CreateCustomerAsync();
    var plan = await CreatePoolPlanAsync(gifts:
    [
      new(null, "Service", _beard.Id, null, 1, "EveryPurchase"),
      new(null, "Physical", null, "Gorra", 1, "EveryPurchase"),
    ]);
    var active = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today, ActivateNow: true));
    var pending = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today));
    var income = await AddMembershipSaleIncomeAsync(450000);
    var memberships = new[] { active.Id, pending.Id };
    var before = await MembershipFingerprintsAsync(memberships);
    var incomeBefore = await IncomeFingerprintAsync(income.Id);
    var incomeService = new IncomeManagementService(_dbContext, TimeProvider.System);

    await _service.LinkIncomeAsync(_adminId, active.Id,
        new MembershipIncomeLinkRequest(income.Id, [pending.Id], "Descuento de 50.000 por pagar las dos"));

    Assert.Equal(before, await MembershipFingerprintsAsync(memberships));
    Assert.Equal(incomeBefore, await IncomeFingerprintAsync(income.Id));
    await Assert.ThrowsAsync<ConflictException>(() => incomeService.SoftDeleteAsync(income.Id));

    await _service.UnlinkIncomeAsync(_adminId, active.Id, new MembershipIncomeUnlinkRequest("La activa se pagó aparte"));

    Assert.Equal(before, await MembershipFingerprintsAsync(memberships));
    Assert.Equal(incomeBefore, await IncomeFingerprintAsync(income.Id));
    // Still blocked: one membership remains on the income.
    await Assert.ThrowsAsync<ConflictException>(() => incomeService.SoftDeleteAsync(income.Id));
    Assert.Equal(incomeBefore, await IncomeFingerprintAsync(income.Id));

    await _service.UnlinkIncomeAsync(_adminId, pending.Id, new MembershipIncomeUnlinkRequest());

    Assert.Equal(before, await MembershipFingerprintsAsync(memberships));
    Assert.Equal(incomeBefore, await IncomeFingerprintAsync(income.Id));

    // The justifications and the whole sequence stay in each membership's history.
    var activeHistory = (await _service.GetByIdAsync(active.Id)).Events;
    Assert.Equal(["IncomeUnlinked", "IncomeLinked", "Activated", "Sold"], activeHistory.Select(entry => entry.Type));
    Assert.Equal("Descuento de 50.000 por pagar las dos", activeHistory.Single(entry => entry.Type == "IncomeLinked").Reason);
    Assert.Equal("La activa se pagó aparte", activeHistory.Single(entry => entry.Type == "IncomeUnlinked").Reason);
    Assert.All(activeHistory.Where(entry => entry.Type.StartsWith("Income")), entry => Assert.Equal(income.Id, entry.IncomeEntryId));
    var pendingHistory = (await _service.GetByIdAsync(pending.Id)).Events;
    Assert.Equal(["IncomeUnlinked", "IncomeLinked", "Sold"], pendingHistory.Select(entry => entry.Type));
    Assert.Equal("Descuento de 50.000 por pagar las dos", pendingHistory.Single(entry => entry.Type == "IncomeLinked").Reason);

    // Once nothing is linked the income can be deleted, and the memberships are still untouched.
    await incomeService.SoftDeleteAsync(income.Id);
    Assert.Equal(before, await MembershipFingerprintsAsync(memberships));
    Assert.Equal(1, await _dbContext.IncomeEntries.CountAsync());
  }

  [Fact]
  public async Task SoldGifts_StayWithTheMembership_AfterThePlanIsEditedOrDeactivated()
  {
    var customer = await CreateCustomerAsync();
    var plan = await CreatePoolPlanAsync(gifts:
    [
      new(null, "Service", _beard.Id, null, 2, "EveryPurchase"),
      new(null, "Physical", null, "Gorra", 1, "FirstPurchaseOfPlan"),
    ]);
    var sold = await _service.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today, ActivateNow: true));
    var before = await MembershipFingerprintsAsync([sold.Id]);
    Assert.Equal(["Granted", "PendingDelivery"], sold.Gifts.Select(gift => gift.Status));

    // Every gift rule replaced and the included services changed.
    await _plans.UpdateAsync(plan.Id, new MembershipPlanUpsertRequest(
        "Oro", "Diez servicios", 250000, "Pool", 10, 90, Terms, null,
        [new(_haircut.Id, null)], [new(null, "Physical", null, "Champú", 1, "EveryPurchase")]));

    Assert.Equal(before, await MembershipFingerprintsAsync([sold.Id]));

    await _plans.UpdateStatusAsync(plan.Id, new MembershipPlanStatusUpdateRequest(false));

    Assert.Equal(before, await MembershipFingerprintsAsync([sold.Id]));
    var reloaded = await _service.GetByIdAsync(sold.Id);
    Assert.Equal(sold.Gifts, reloaded.Gifts);
    Assert.Equal(2, await _dbContext.MembershipGifts.CountAsync(gift => gift.CustomerMembershipId == sold.Id));
    var giftLine = Assert.Single(reloaded.Allowances, line => line.Origin == "Gift");
    Assert.Equal(reloaded.Gifts.Single(gift => gift.Kind == "Service").Id, giftLine.GiftId);
    Assert.Equal(2, giftLine.GrantedQuantity);
    Assert.Equal("Barba", Assert.Single(giftLine.Services).ServiceName);
  }

  [Fact]
  public async Task MergingCustomers_MovesTheirMemberships_KeepingTheOriginalHolderAndHistory()
  {
    var target = await CreateCustomerAsync("Destino");
    var source = await CreateCustomerAsync("Origen");
    var plan = await CreatePoolPlanAsync(gifts: [new(null, "Physical", null, "Gorra", 1, "FirstPurchaseOfPlan")]);
    var sourceMembership = await _service.SellAsync(_adminId, source.Id, new MembershipSaleRequest(plan.Id, _today, ActivateNow: true));
    await _service.SellAsync(_adminId, target.Id, new MembershipSaleRequest(plan.Id, _today));

    var preview = await _customers.GetMergePreviewAsync(target.Id, source.Id);
    Assert.Contains(preview.Warnings, warning => warning.Contains("1 membresía(s)"));
    Assert.Contains(preview.Warnings, warning => warning.Contains("primera compra") && warning.Contains("Oro"));

    await _customers.MergeAsync(_adminId, target.Id, new CustomerMergeRequest(source.Id));

    Assert.Empty(await _service.GetCustomerMembershipsAsync(source.Id));
    Assert.Equal(2, (await _service.GetCustomerMembershipsAsync(target.Id)).Count);

    var moved = await _service.GetByIdAsync(sourceMembership.Id);
    Assert.Equal(target.Id, moved.CustomerId);
    Assert.Equal(source.Id, moved.OriginalCustomerId);
    Assert.Equal("Origen", moved.OriginalCustomerName);
    Assert.Equal("Active", moved.Status);
    Assert.Equal(Balances(sourceMembership), Balances(moved));
    Assert.Equal(sourceMembership.Gifts, moved.Gifts);
    var mergeEvent = Assert.Single(moved.Events, membershipEvent => membershipEvent.Type == "MovedByMerge");
    Assert.Equal(source.Id, mergeEvent.RelatedCustomerId);
    Assert.Contains(moved.Events, membershipEvent => membershipEvent.Type == "Sold");

    // After the merge both purchases count for the surviving profile: no new first-purchase gift.
    var afterMerge = await _service.SellAsync(_adminId, target.Id, new MembershipSaleRequest(plan.Id, _today));
    Assert.Empty(afterMerge.Gifts);
  }

  /// <summary>Allowance views hold a nested list, which record equality compares by reference.</summary>
  private static IEnumerable<string> Balances(CustomerMembershipDetailView membership)
      => membership.Allowances.Select(line =>
          $"{line.Id}|{line.Origin}|{line.GiftId}|{line.GrantedQuantity}|{line.AvailableQuantity}|{string.Join(",", line.Services.Select(service => service.ServiceName))}");

  /// <summary>Everything a sold membership promises: what was paid, its status, balances and gifts (not the income link).</summary>
  private async Task<IReadOnlyList<string>> MembershipFingerprintsAsync(IEnumerable<Guid> membershipIds)
  {
    var fingerprints = new List<string>();
    foreach (var id in membershipIds)
    {
      var membership = await _service.GetByIdAsync(id);
      var gifts = membership.Gifts.Select(gift =>
          $"{gift.Id}|{gift.Kind}|{gift.Condition}|{gift.Quantity}|{gift.ServiceName}|{gift.Description}|{gift.Status}");
      fingerprints.Add(string.Join(" / ",
          [
            $"{membership.PlanName}|{membership.Price}|{membership.Mode}|{membership.Terms}|{membership.SpecificConditions}",
            $"{membership.Status}|{membership.DisplayStatus}|{membership.ActivatedAt:O}|{membership.SoldOn}|{membership.StartsOn}|{membership.EndsOn}",
            .. Balances(membership),
            .. gifts,
          ]));
    }

    return fingerprints;
  }

  private async Task<string> IncomeFingerprintAsync(Guid incomeId)
  {
    var income = await _dbContext.IncomeEntries.AsNoTracking().SingleAsync(entry => entry.Id == incomeId);
    var count = await _dbContext.IncomeEntries.CountAsync();
    return $"{count}|{income.Kind}|{income.Amount}|{income.OccurredOn}|{income.IsDeleted}|{income.ServiceId}|{income.StaffProfileId}|{income.UpdatedAt:O}";
  }

  private async Task<CustomerDetailView> CreateCustomerAsync(string name = "Carlos Pérez")
      => await _customers.CreateAsync(_adminId, new CustomerCreateRequest(name, "3001234567", $"{Guid.NewGuid():N}@example.com"));

  private async Task<MembershipPlanView> CreatePoolPlanAsync(
      string name = "Oro",
      int durationDays = 90,
      IReadOnlyList<MembershipPlanGiftRuleRequest>? gifts = null)
      => await _plans.CreateAsync(new MembershipPlanUpsertRequest(
          name, "Diez servicios", 250000, "Pool", 10, durationDays, Terms, "Solo de lunes a jueves",
          [new(_haircut.Id, null), new(_beard.Id, null)], gifts ?? []));

  private async Task<IncomeEntry> AddMembershipSaleIncomeAsync(int amount)
  {
    var income = IncomeEntry.CreateMembershipSale(amount, _today, _adminId, DateTime.UtcNow);
    _dbContext.IncomeEntries.Add(income);
    await _dbContext.SaveChangesAsync();
    return income;
  }
}
