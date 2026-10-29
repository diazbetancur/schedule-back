using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Domain.Common;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Memberships;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Customers;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Barbershop.Tests.Features.Memberships;

public sealed class MembershipLedgerServiceTests : IDisposable
{
  private const string Terms = "La membresía es personal e intransferible.";

  private readonly DateOnly _today = DateOnly.FromDateTime(BogotaClock.ToLocal(DateTime.UtcNow));
  private readonly AppDbContext _dbContext;
  private readonly IAdminMembershipsService _memberships;
  private readonly IAdminMembershipLedgerService _ledger;
  private readonly IAdminMembershipPlansService _plans;
  private readonly IAdminCustomersService _customers;
  private readonly Guid _adminId;
  private readonly Service _haircut;
  private readonly Service _beard;
  private readonly Service _dye;
  private readonly StaffProfile _barber;

  public MembershipLedgerServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        // The in-memory provider has no transactions; consumptions open one for PostgreSQL.
        .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options;

    _dbContext = new AppDbContext(options);
    _memberships = new MembershipSalesService(_dbContext, TimeProvider.System);
    _ledger = new MembershipLedgerService(_dbContext, _memberships, TimeProvider.System);
    _plans = new MembershipPlanManagementService(_dbContext, TimeProvider.System);
    _customers = new CustomerManagementService(_dbContext, TimeProvider.System);

    var admin = new User("Admin Principal", "admin@example.com", "hash", DateTime.UtcNow);
    var barberUser = new User("Alex Barbero", "alex@example.com", "hash", DateTime.UtcNow);
    _haircut = new Service("Corte", 25000, DateTime.UtcNow, 30);
    _beard = new Service("Barba", 15000, DateTime.UtcNow, 30);
    _dye = new Service("Tinte", 40000, DateTime.UtcNow, 30);
    _dbContext.Users.AddRange(admin, barberUser);
    _dbContext.Services.AddRange(_haircut, _beard, _dye);
    _barber = new StaffProfile(barberUser.Id, "Alex", 30, DateTime.UtcNow);
    _dbContext.StaffProfiles.Add(_barber);
    _dbContext.SaveChanges();
    _adminId = admin.Id;
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task GiftedUnitsAreUsedBeforePurchasedOnes()
  {
    var membership = await SellActiveAsync(await CreatePoolPlanAsync(gifts: [new(null, "Service", _beard.Id, null, 2, "EveryPurchase")]));

    var afterFirst = await ConsumeAsync(membership.Id, _beard.Id, 1);

    var consumption = Assert.Single(afterFirst.Consumptions);
    Assert.Equal("Gift", Assert.Single(consumption.Parts).Origin);
    Assert.Equal(["Purchased:10/10", "Gift:1/2"], Lines(afterFirst));
  }

  [Fact]
  public async Task OneConsumption_CanUseGiftedAndPurchasedUnits_AsASingleOperation()
  {
    var membership = await SellActiveAsync(await CreatePoolPlanAsync(gifts: [new(null, "Service", _beard.Id, null, 2, "EveryPurchase")]));

    var after = await ConsumeAsync(membership.Id, _beard.Id, 3, staffId: _barber.Id);

    var consumption = Assert.Single(after.Consumptions);
    Assert.Equal(3, consumption.Quantity);
    Assert.Equal("Alex", consumption.StaffName);
    Assert.Equal("Admin Principal", consumption.RegisteredByName);
    Assert.Equal(["Gift:2", "Purchased:1"], consumption.Parts.Select(part => $"{part.Origin}:{part.Quantity}"));
    Assert.Equal(["Purchased:9/10", "Gift:0/2"], Lines(after));
    Assert.Equal(1, await _dbContext.MembershipConsumptions.CountAsync());
  }

  [Fact]
  public async Task PoolPlan_AnyIncludedServiceUsesTheSharedBalance()
  {
    var membership = await SellActiveAsync(await CreatePoolPlanAsync());

    await ConsumeAsync(membership.Id, _haircut.Id, 4);
    var after = await ConsumeAsync(membership.Id, _beard.Id, 6);

    Assert.Equal(["Purchased:0/10"], Lines(after));
    var exhausted = await Assert.ThrowsAsync<ValidationProblemException>(() => ConsumeAsync(membership.Id, _haircut.Id, 1));
    Assert.Contains("quantity", exhausted.Errors.Keys);
  }

  [Fact]
  public async Task PerServicePlan_EachServiceOnlyUsesItsOwnBalance()
  {
    var plan = await _plans.CreateAsync(new MembershipPlanUpsertRequest(
        "Plata", null, 120000, "PerService", null, 30, Terms, null, [new(_haircut.Id, 2), new(_beard.Id, 3)], []));
    var membership = await SellActiveAsync(plan);

    var after = await ConsumeAsync(membership.Id, _haircut.Id, 2);
    Assert.Equal(["Purchased:3/3", "Purchased:0/2"], Lines(after)); // lines are ordered by service name: Barba, Corte

    // Barba still has 3, but they cannot pay for a haircut.
    var insufficient = await Assert.ThrowsAsync<ValidationProblemException>(() => ConsumeAsync(membership.Id, _haircut.Id, 1));
    Assert.Contains("quantity", insufficient.Errors.Keys);
    after = await ConsumeAsync(membership.Id, _beard.Id, 3);
    Assert.Equal(["Purchased:0/3", "Purchased:0/2"], Lines(after));
  }

  [Fact]
  public async Task SeveralConsumptions_AddUp_AndAreListedNewestFirst()
  {
    var membership = await SellActiveAsync(await CreatePoolPlanAsync(), startsOn: _today.AddDays(-5));

    await ConsumeAsync(membership.Id, _haircut.Id, 1, _today.AddDays(-5));
    await ConsumeAsync(membership.Id, _beard.Id, 2, _today.AddDays(-1));
    var after = await ConsumeAsync(membership.Id, _haircut.Id, 3, _today);

    Assert.Equal([_today, _today.AddDays(-1), _today.AddDays(-5)], after.Consumptions.Select(consumption => consumption.ServiceDate));
    Assert.Equal(["Purchased:4/10"], Lines(after));
    Assert.Equal(6, after.Allowances.Single().ConsumedQuantity);
  }

  [Fact]
  public async Task InsufficientBalance_IsRejected_AndNothingIsStored()
  {
    var membership = await SellActiveAsync(await CreatePoolPlanAsync(gifts: [new(null, "Service", _beard.Id, null, 1, "EveryPurchase")]));

    var error = await Assert.ThrowsAsync<ValidationProblemException>(() => ConsumeAsync(membership.Id, _beard.Id, 12));

    Assert.Contains("quedan 11", error.Errors["quantity"][0]);
    Assert.Empty(await _dbContext.MembershipConsumptions.ToListAsync());
    Assert.Empty(await _dbContext.MembershipMovements.ToListAsync());
  }

  [Fact]
  public async Task InvalidQuantitiesServicesAndStaff_AreRejected()
  {
    var membership = await SellActiveAsync(await CreatePoolPlanAsync());

    foreach (var quantity in new[] { 0, -1, MembershipConsumption.MaxQuantity + 1 })
    {
      var error = await Assert.ThrowsAsync<ValidationProblemException>(() => ConsumeAsync(membership.Id, _haircut.Id, quantity));
      Assert.Contains("quantity", error.Errors.Keys);
    }

    var notIncluded = await Assert.ThrowsAsync<ValidationProblemException>(() => ConsumeAsync(membership.Id, _dye.Id, 1));
    Assert.Contains("serviceId", notIncluded.Errors.Keys);
    var unknownStaff = await Assert.ThrowsAsync<ValidationProblemException>(() => ConsumeAsync(membership.Id, _haircut.Id, 1, staffId: Guid.NewGuid()));
    Assert.Contains("staffProfileId", unknownStaff.Errors.Keys);
    var noKey = await Assert.ThrowsAsync<ValidationProblemException>(() => _ledger.ConsumeAsync(
        _adminId, membership.Id, new MembershipConsumptionRequest(_haircut.Id, 1, _today, null, Guid.Empty)));
    Assert.Contains("idempotencyKey", noKey.Errors.Keys);
    Assert.Empty(await _dbContext.MembershipConsumptions.ToListAsync());
  }

  [Fact]
  public async Task ServiceDate_MustBeWithinTheValidity_IncludingTheLastDay_AndNotInTheFuture()
  {
    var endsToday = await SellActiveAsync(await CreatePoolPlanAsync(), startsOn: _today.AddDays(-29), endsOn: _today);

    await ConsumeAsync(endsToday.Id, _haircut.Id, 1, _today); // last day, inclusive
    await ConsumeAsync(endsToday.Id, _haircut.Id, 1, _today.AddDays(-29)); // first day
    var future = await Assert.ThrowsAsync<ValidationProblemException>(() => ConsumeAsync(endsToday.Id, _haircut.Id, 1, _today.AddDays(1)));
    Assert.Contains("futura", future.Errors["serviceDate"][0]);
    var beforeStart = await Assert.ThrowsAsync<ValidationProblemException>(() => ConsumeAsync(endsToday.Id, _haircut.Id, 1, _today.AddDays(-30)));
    Assert.Contains("vigencia", beforeStart.Errors["serviceDate"][0]);
  }

  [Fact]
  public async Task RetroactiveConsumption_IsAllowedAfterExpiry_OnlyForServicesGivenWithinTheValidity()
  {
    var expired = await SellActiveAsync(await CreatePoolPlanAsync(), startsOn: _today.AddDays(-40), endsOn: _today.AddDays(-10));
    Assert.Equal("Expired", expired.DisplayStatus);

    var after = await ConsumeAsync(expired.Id, _haircut.Id, 2, _today.AddDays(-10));
    Assert.Equal(_today.AddDays(-10), Assert.Single(after.Consumptions).ServiceDate);

    var afterExpiry = await Assert.ThrowsAsync<ValidationProblemException>(() => ConsumeAsync(expired.Id, _haircut.Id, 1, _today.AddDays(-9)));
    Assert.Contains("vigencia", afterExpiry.Errors["serviceDate"][0]);
  }

  [Fact]
  public async Task PendingOrAnnulledMemberships_CannotBeConsumed()
  {
    var plan = await CreatePoolPlanAsync();
    var customer = await CreateCustomerAsync();
    var pending = await _memberships.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today));
    await Assert.ThrowsAsync<ConflictException>(() => ConsumeAsync(pending.Id, _haircut.Id, 1));

    var annulled = await SellActiveAsync(plan);
    await _memberships.AnnulAsync(_adminId, annulled.Id, new MembershipAnnulRequest("Venta equivocada"));
    await Assert.ThrowsAsync<ConflictException>(() => ConsumeAsync(annulled.Id, _haircut.Id, 1));

    Assert.Empty(await _dbContext.MembershipConsumptions.ToListAsync());
  }

  [Fact]
  public async Task RetriedRequest_WithTheSameKey_IsRegisteredOnce()
  {
    var membership = await SellActiveAsync(await CreatePoolPlanAsync());
    var request = new MembershipConsumptionRequest(_haircut.Id, 2, _today, _barber.Id, Guid.NewGuid());

    var first = await _ledger.ConsumeAsync(_adminId, membership.Id, request);
    var retried = await _ledger.ConsumeAsync(_adminId, membership.Id, request);

    Assert.Equal(first.Consumptions.Single().Id, Assert.Single(retried.Consumptions).Id);
    Assert.Equal(["Purchased:8/10"], Lines(retried));
    Assert.Equal(1, await _dbContext.MembershipConsumptions.CountAsync());

    // Reusing a key for different data is refused rather than silently ignored.
    await Assert.ThrowsAsync<ConflictException>(() => _ledger.ConsumeAsync(_adminId, membership.Id, request with { Quantity = 3 }));
    Assert.Equal(1, await _dbContext.MembershipConsumptions.CountAsync());
  }

  [Fact]
  public async Task Reversal_GivesTheUnitsBackToTheSameLines_KeepsTheOriginal_AndHappensOnlyOnce()
  {
    var membership = await SellActiveAsync(await CreatePoolPlanAsync(gifts: [new(null, "Service", _beard.Id, null, 2, "EveryPurchase")]));
    var consumed = await ConsumeAsync(membership.Id, _beard.Id, 3);
    var consumptionId = consumed.Consumptions.Single().Id;

    var noReason = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _ledger.ReverseAsync(_adminId, membership.Id, consumptionId, new MembershipConsumptionReverseRequest(" ")));
    Assert.Contains("reason", noReason.Errors.Keys);

    var reversed = await _ledger.ReverseAsync(_adminId, membership.Id, consumptionId, new MembershipConsumptionReverseRequest("Se registró en la membresía equivocada"));

    Assert.Equal(["Purchased:10/10", "Gift:2/2"], Lines(reversed));
    var original = Assert.Single(reversed.Consumptions);
    Assert.Equal(3, original.Quantity);
    Assert.Equal(["Gift:2", "Purchased:1"], original.Parts.Select(part => $"{part.Origin}:{part.Quantity}"));
    Assert.Equal("Se registró en la membresía equivocada", original.Reversal!.Reason);
    Assert.Equal("Admin Principal", original.Reversal.PerformedByName);
    Assert.Equal(4, await _dbContext.MembershipMovements.CountAsync()); // 2 taken + 2 given back

    await Assert.ThrowsAsync<ConflictException>(() =>
        _ledger.ReverseAsync(_adminId, membership.Id, consumptionId, new MembershipConsumptionReverseRequest("Otra vez")));
    await Assert.ThrowsAsync<NotFoundException>(() =>
        _ledger.ReverseAsync(_adminId, membership.Id, Guid.NewGuid(), new MembershipConsumptionReverseRequest("No existe")));
    Assert.Equal(1, await _dbContext.MembershipConsumptionReversals.CountAsync());
  }

  [Fact]
  public async Task ReversalOnAnAnnulledMembership_IsAllowed_ButDoesNotEnableItsBenefits()
  {
    var membership = await SellActiveAsync(await CreatePoolPlanAsync());
    var consumed = await ConsumeAsync(membership.Id, _haircut.Id, 2);
    await _memberships.AnnulAsync(_adminId, membership.Id, new MembershipAnnulRequest("Retracto del cliente"));

    var reversed = await _ledger.ReverseAsync(
        _adminId, membership.Id, consumed.Consumptions.Single().Id, new MembershipConsumptionReverseRequest("El corte no se hizo"));

    Assert.Equal("Annulled", reversed.Status);
    Assert.Equal(["Purchased:10/10"], Lines(reversed));
    await Assert.ThrowsAsync<ConflictException>(() => ConsumeAsync(membership.Id, _haircut.Id, 1));
  }

  [Fact]
  public async Task OutOfOrderServiceDates_AreAllocatedInRegistrationOrder_AndReversalsReturnExactlyWhatEachTook()
  {
    var membership = await SellActiveAsync(
        await CreatePoolPlanAsync(gifts: [new(null, "Service", _beard.Id, null, 2, "EveryPurchase")]), startsOn: _today.AddDays(-10));

    // Registered first, but dated later: it takes the first gifted unit.
    var later = (await ConsumeAsync(membership.Id, _beard.Id, 1, _today)).Consumptions.Single();
    // Registered second, dated earlier: it takes the remaining gift and then one purchased unit.
    var afterEarlier = await ConsumeAsync(membership.Id, _beard.Id, 2, _today.AddDays(-5));
    var earlier = afterEarlier.Consumptions.Single(consumption => consumption.Id != later.Id);

    Assert.Equal(["Gift:1"], Parts(later));
    Assert.Equal(["Gift:1", "Purchased:1"], Parts(earlier));
    // The first operation was not recomputed because an earlier-dated one arrived after it.
    Assert.Equal(["Gift:1"], Parts(afterEarlier.Consumptions.Single(consumption => consumption.Id == later.Id)));
    // The history keeps the real service dates (newest service first).
    Assert.Equal([_today, _today.AddDays(-5)], afterEarlier.Consumptions.Select(consumption => consumption.ServiceDate));
    Assert.Equal(["Purchased:9/10", "Gift:0/2"], Lines(afterEarlier));

    // Reversing the later-dated one gives back only its gifted unit.
    var afterFirstReversal = await _ledger.ReverseAsync(_adminId, membership.Id, later.Id, new MembershipConsumptionReverseRequest("Duplicado"));
    Assert.Equal(["Purchased:9/10", "Gift:1/2"], Lines(afterFirstReversal));
    Assert.Equal(["Gift:1", "Purchased:1"], Parts(afterFirstReversal.Consumptions.Single(consumption => consumption.Id == earlier.Id)));

    // A new consumption uses the returned gift first; the earlier operation is still untouched.
    var afterNew = await ConsumeAsync(membership.Id, _beard.Id, 1, _today.AddDays(-7));
    var newest = afterNew.Consumptions.Single(consumption => consumption.Id != later.Id && consumption.Id != earlier.Id);
    Assert.Equal(["Gift:1"], Parts(newest));
    Assert.Equal(["Purchased:9/10", "Gift:0/2"], Lines(afterNew));

    // Reversing the earlier-dated one returns exactly its gift + purchased split.
    var final = await _ledger.ReverseAsync(_adminId, membership.Id, earlier.Id, new MembershipConsumptionReverseRequest("Registrado por error"));
    Assert.Equal(["Purchased:10/10", "Gift:1/2"], Lines(final));
    Assert.Equal(["Gift:1", "Purchased:1"], Parts(final.Consumptions.Single(consumption => consumption.Id == earlier.Id)));

    var movements = await _dbContext.MembershipMovements.AsNoTracking()
        .Select(movement => new { movement.MembershipConsumptionId, movement.Type, movement.Quantity })
        .ToListAsync();
    Assert.Equal(7, movements.Count); // later: 1 + 1 back; earlier: 2 + 2 back; newest: 1
    foreach (var consumptionId in new[] { later.Id, earlier.Id })
    {
      Assert.Equal(
          movements.Where(m => m.MembershipConsumptionId == consumptionId && m.Type == MembershipMovementType.Consumption).Sum(m => m.Quantity),
          movements.Where(m => m.MembershipConsumptionId == consumptionId && m.Type == MembershipMovementType.Reversal).Sum(m => m.Quantity));
    }
  }

  [Fact]
  public async Task AnnulledMembership_RejectsServicesDatedBeforeTheAnnulment_AndAReversalUnblocksNothing()
  {
    var membership = await SellActiveAsync(
        await CreatePoolPlanAsync(gifts: [new(null, "Service", _beard.Id, null, 1, "EveryPurchase")]), startsOn: _today.AddDays(-10));
    var consumed = await ConsumeAsync(membership.Id, _haircut.Id, 2, _today.AddDays(-6));
    await _memberships.AnnulAsync(_adminId, membership.Id, new MembershipAnnulRequest("Retracto del cliente"));

    // A service given while the membership was still active cannot be registered after the annulment.
    await Assert.ThrowsAsync<ConflictException>(() => ConsumeAsync(membership.Id, _haircut.Id, 1, _today.AddDays(-8)));
    await Assert.ThrowsAsync<ConflictException>(() => ConsumeAsync(membership.Id, _beard.Id, 1, _today.AddDays(-3)));

    var reversed = await _ledger.ReverseAsync(
        _adminId, membership.Id, consumed.Consumptions.Single().Id, new MembershipConsumptionReverseRequest("El corte no se hizo"));

    Assert.Equal("Annulled", reversed.Status);
    Assert.Equal("Annulled", reversed.DisplayStatus);
    Assert.Equal("Blocked", Assert.Single(reversed.Gifts).Status);
    Assert.Equal(["Purchased:10/10", "Gift:1/1"], Lines(reversed)); // units are back, but still blocked
    Assert.DoesNotContain(reversed.Events, membershipEvent => membershipEvent.Type is "Activated" && membershipEvent.PerformedAt > reversed.AnnulledAt);

    // Still blocked after the reversal, whatever the date.
    await Assert.ThrowsAsync<ConflictException>(() => ConsumeAsync(membership.Id, _haircut.Id, 1, _today.AddDays(-8)));
    await Assert.ThrowsAsync<ConflictException>(() => ConsumeAsync(membership.Id, _beard.Id, 1, _today));
    Assert.Equal(1, await _dbContext.MembershipConsumptions.CountAsync());
  }

  [Fact]
  public async Task Consuming_CreatesNoIncome_NoCommission_AndLeavesTheSaleAndOtherMembershipsUntouched()
  {
    var plan = await CreatePoolPlanAsync(gifts: [new(null, "Physical", null, "Gorra", 1, "EveryPurchase")]);
    var membership = await SellActiveAsync(plan);
    var other = await SellActiveAsync(plan);
    var income = IncomeEntry.CreateMembershipSale(500000, _today, _adminId, DateTime.UtcNow);
    _dbContext.IncomeEntries.Add(income);
    await _dbContext.SaveChangesAsync();
    await _memberships.LinkIncomeAsync(_adminId, membership.Id, new MembershipIncomeLinkRequest(income.Id, [other.Id]));
    var incomesBefore = await _dbContext.IncomeEntries.AsNoTracking().Select(entry => $"{entry.Id}|{entry.Amount}|{entry.Kind}|{entry.StaffProfileId}|{entry.UpdatedAt:O}").ToListAsync();
    var appointmentsBefore = await _dbContext.Appointments.CountAsync();
    var sale = await _memberships.GetByIdAsync(membership.Id);

    var after = await ConsumeAsync(membership.Id, _haircut.Id, 3, staffId: _barber.Id);
    after = await _ledger.ReverseAsync(_adminId, membership.Id, after.Consumptions.Single().Id, new MembershipConsumptionReverseRequest("Prueba de reverso"));
    after = await ConsumeAsync(membership.Id, _haircut.Id, 1, staffId: _barber.Id);

    // No income, professional share or appointment comes out of a consumption.
    Assert.Equal(incomesBefore, await _dbContext.IncomeEntries.AsNoTracking().Select(entry => $"{entry.Id}|{entry.Amount}|{entry.Kind}|{entry.StaffProfileId}|{entry.UpdatedAt:O}").ToListAsync());
    Assert.Equal(appointmentsBefore, await _dbContext.Appointments.CountAsync());

    // The sold conditions and gifts stay as they were.
    Assert.Equal(sale.Price, after.Price);
    Assert.Equal(sale.Terms, after.Terms);
    Assert.Equal(sale.Status, after.Status);
    Assert.Equal(sale.Gifts, after.Gifts);
    Assert.Equal(sale.Income!.IncomeEntryId, after.Income!.IncomeEntryId);
    Assert.Equal(sale.Events.Count, after.Events.Count);

    // The other membership of the same customer and plan keeps its whole balance.
    Assert.Equal(["Purchased:10/10"], Lines(await _memberships.GetByIdAsync(other.Id)));
    Assert.Equal(["Purchased:9/10"], Lines(after));
  }

  [Fact]
  public async Task MergingCustomers_KeepsTheConsumptionsAndBalancesOfTheMovedMemberships()
  {
    var plan = await CreatePoolPlanAsync();
    var source = await CreateCustomerAsync("Origen");
    var target = await CreateCustomerAsync("Destino");
    var membership = await _memberships.SellAsync(_adminId, source.Id, new MembershipSaleRequest(plan.Id, _today, ActivateNow: true));
    var consumed = await ConsumeAsync(membership.Id, _haircut.Id, 2);

    await _customers.MergeAsync(_adminId, target.Id, new CustomerMergeRequest(source.Id));

    var moved = await _memberships.GetByIdAsync(membership.Id);
    Assert.Equal(target.Id, moved.CustomerId);
    Assert.Equal(consumed.Consumptions.Single().Id, Assert.Single(moved.Consumptions).Id);
    Assert.Equal(["Purchased:8/10"], Lines(moved));
  }

  private static IEnumerable<string> Lines(CustomerMembershipDetailView membership)
      => membership.Allowances.Select(line => $"{line.Origin}:{line.AvailableQuantity}/{line.GrantedQuantity}");

  private static IEnumerable<string> Parts(MembershipConsumptionView consumption)
      => consumption.Parts.Select(part => $"{part.Origin}:{part.Quantity}");

  private Task<CustomerMembershipDetailView> ConsumeAsync(Guid membershipId, Guid serviceId, int quantity, DateOnly? serviceDate = null, Guid? staffId = null)
      => _ledger.ConsumeAsync(_adminId, membershipId, new MembershipConsumptionRequest(serviceId, quantity, serviceDate ?? _today, staffId, Guid.NewGuid()));

  private async Task<CustomerMembershipDetailView> SellActiveAsync(MembershipPlanView plan, DateOnly? startsOn = null, DateOnly? endsOn = null)
  {
    var customer = await CreateCustomerAsync();
    var expired = endsOn is { } end && end < _today;
    return await _memberships.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(
        plan.Id, startsOn ?? _today, endsOn, ActivateNow: true,
        ConfirmExpiredActivation: expired, ActivationReason: expired ? "Venta registrada tarde" : null));
  }

  private async Task<CustomerDetailView> CreateCustomerAsync(string name = "Carlos Pérez")
      => await _customers.CreateAsync(_adminId, new CustomerCreateRequest(name, "3001234567", $"{Guid.NewGuid():N}@example.com"));

  private async Task<MembershipPlanView> CreatePoolPlanAsync(IReadOnlyList<MembershipPlanGiftRuleRequest>? gifts = null)
      => await _plans.CreateAsync(new MembershipPlanUpsertRequest(
          $"Oro {Guid.NewGuid():N}"[..12], "Diez servicios", 250000, "Pool", 10, 90, Terms, null,
          [new(_haircut.Id, null), new(_beard.Id, null)], gifts ?? []));
}
