using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Application.Memberships.Portal;
using Barbershop.Domain.Common;
using Barbershop.Domain.Services;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Customers;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Barbershop.Tests.Features.Memberships;

public sealed class MyMembershipsServiceTests : IDisposable
{
  private const string Terms = "La membresía es personal e intransferible.";

  private readonly DateOnly _today = DateOnly.FromDateTime(BogotaClock.ToLocal(DateTime.UtcNow));
  private readonly AppDbContext _dbContext;
  private readonly IAdminMembershipsService _memberships;
  private readonly IAdminMembershipLedgerService _ledger;
  private readonly IAdminMembershipGiftDeliveryService _deliveries;
  private readonly IAdminMembershipPlansService _plans;
  private readonly IAdminCustomersService _customers;
  private readonly IMyMembershipsService _mine;
  private readonly Guid _adminId;
  private readonly Service _haircut;
  private readonly Service _beard;

  public MyMembershipsServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options;

    _dbContext = new AppDbContext(options);
    _memberships = new MembershipSalesService(_dbContext, TimeProvider.System);
    _ledger = new MembershipLedgerService(_dbContext, _memberships, TimeProvider.System);
    _deliveries = new MembershipGiftDeliveryService(_dbContext, _memberships, TimeProvider.System);
    _plans = new MembershipPlanManagementService(_dbContext, TimeProvider.System);
    _customers = new CustomerManagementService(_dbContext, TimeProvider.System);
    _mine = new MyMembershipsService(_dbContext, _memberships);

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
  public async Task UserWithoutALinkedProfile_SeesNothing()
  {
    var user = await CreateUserAsync();
    var stranger = await CreateCustomerAsync(); // a profile exists, but not linked to this user
    var plan = await CreatePlanAsync();
    var membership = await SellAsync(stranger.Id, plan, activate: true);

    Assert.Empty(await _mine.GetMineAsync(user));
    await Assert.ThrowsAsync<NotFoundException>(() => _mine.GetMineByIdAsync(user, membership.Id));
  }

  [Fact]
  public async Task LinkedUserWithoutMemberships_GetsAnEmptyList()
  {
    var user = await CreateUserAsync();
    await CreateCustomerAsync(linkedUserId: user);

    Assert.Empty(await _mine.GetMineAsync(user));
  }

  [Fact]
  public async Task AnotherCustomersMembership_IsNotFound_EvenWithItsExactId()
  {
    var user = await CreateUserAsync();
    var mine = await CreateCustomerAsync(linkedUserId: user);
    var otherUser = await CreateUserAsync();
    var other = await CreateCustomerAsync(linkedUserId: otherUser);
    var plan = await CreatePlanAsync();
    var own = await SellAsync(mine.Id, plan, activate: true);
    var foreign = await SellAsync(other.Id, plan, activate: true);

    Assert.Equal([own.Id], (await _mine.GetMineAsync(user)).Select(summary => summary.Id));
    await Assert.ThrowsAsync<NotFoundException>(() => _mine.GetMineByIdAsync(user, foreign.Id));
    await Assert.ThrowsAsync<NotFoundException>(() => _mine.GetMineByIdAsync(user, Guid.NewGuid()));
  }

  [Fact]
  public async Task EveryStatus_IsListed_WithTheSameStatusTheAdminSees()
  {
    var user = await CreateUserAsync();
    var customer = await CreateCustomerAsync(linkedUserId: user);
    var plan = await CreatePlanAsync();
    var active = await SellAsync(customer.Id, plan, activate: true);
    var pending = await SellAsync(customer.Id, plan, activate: false);
    var expired = await SellAsync(customer.Id, plan, activate: true, startsOn: _today.AddDays(-40), endsOn: _today.AddDays(-10));
    var annulled = await SellAsync(customer.Id, plan, activate: true);
    await _memberships.AnnulAsync(_adminId, annulled.Id, new MembershipAnnulRequest("Retracto del cliente"));

    var statuses = (await _mine.GetMineAsync(user)).ToDictionary(summary => summary.Id, summary => summary.DisplayStatus);

    Assert.Equal("Active", statuses[active.Id]);
    Assert.Equal("PendingActivation", statuses[pending.Id]);
    Assert.Equal("Expired", statuses[expired.Id]);
    Assert.Equal("Annulled", statuses[annulled.Id]);
    foreach (var (id, status) in statuses)
    {
      Assert.Equal((await _memberships.GetByIdAsync(id)).DisplayStatus, status);
      Assert.Equal(status, (await _mine.GetMineByIdAsync(user, id)).DisplayStatus);
    }
  }

  [Fact]
  public async Task Balances_ConsumptionsAndGifts_MatchTheAdminComputation()
  {
    var user = await CreateUserAsync();
    var customer = await CreateCustomerAsync(linkedUserId: user);
    var plan = await CreatePlanAsync();
    var membership = await SellAsync(customer.Id, plan, activate: true);
    var cap = membership.Gifts.Single(gift => gift.Description == "Gorra");
    // 3 beards: 2 gifted + 1 paid; 2 haircuts, reversed; the cap delivered; the keyring pending.
    await Consume(membership.Id, _beard.Id, 3);
    var haircuts = await Consume(membership.Id, _haircut.Id, 2);
    await _ledger.ReverseAsync(_adminId, membership.Id, haircuts.Consumptions.Single(c => c.ServiceId == _haircut.Id).Id,
        new MembershipConsumptionReverseRequest("Registrado por error"));
    await _deliveries.DeliverAsync(_adminId, membership.Id, cap.Id, new MembershipGiftDeliveryRequest(_today, Guid.NewGuid()));

    var view = await _mine.GetMineByIdAsync(user, membership.Id);
    var admin = await _memberships.GetByIdAsync(membership.Id);

    Assert.Equal(
        admin.Allowances.Select(line => $"{line.Origin}:{line.GrantedQuantity}:{line.ConsumedQuantity}:{line.AvailableQuantity}"),
        view.Balances.Select(line => $"{line.Origin}:{line.GrantedQuantity}:{line.ConsumedQuantity}:{line.AvailableQuantity}"));
    Assert.Equal(["Purchased:10:1:9", "Gift:2:2:0"], view.Balances.Select(line => $"{line.Origin}:{line.GrantedQuantity}:{line.ConsumedQuantity}:{line.AvailableQuantity}"));
    Assert.Equal(["Barba", "Corte"], view.Balances[0].Services);

    Assert.Equal(2, view.Consumptions.Count);
    var reversed = view.Consumptions.Single(consumption => consumption.ServiceName == "Corte");
    Assert.True(reversed.IsReversed);
    Assert.NotNull(reversed.ReversedAt);
    var beards = view.Consumptions.Single(consumption => consumption.ServiceName == "Barba");
    Assert.False(beards.IsReversed);
    Assert.Equal(["Gift:2", "Purchased:1"], beards.Parts.Select(part => $"{part.Origin}:{part.Quantity}"));
    Assert.Equal(_today, beards.ServiceDate);

    Assert.Equal(["Service:Barba:Granted:", $"Physical:Gorra:Delivered:{_today}", "Physical:Llavero:PendingDelivery:"],
        view.Gifts.Select(gift => $"{gift.Kind}:{gift.Description}:{gift.Status}:{gift.DeliveredOn}"));

    var summary = Assert.Single(await _mine.GetMineAsync(user));
    Assert.Equal((10, 9, 2, 0, 1), (summary.PurchasedGranted, summary.PurchasedAvailable, summary.GiftedGranted, summary.GiftedAvailable, summary.PendingPhysicalGifts));
  }

  [Fact]
  public async Task AnnulledMembership_KeepsDeliveredGifts_AndShowsTheRestBlocked()
  {
    var user = await CreateUserAsync();
    var customer = await CreateCustomerAsync(linkedUserId: user);
    var membership = await SellAsync(customer.Id, await CreatePlanAsync(), activate: true);
    await _deliveries.DeliverAsync(_adminId, membership.Id, membership.Gifts.Single(gift => gift.Description == "Gorra").Id,
        new MembershipGiftDeliveryRequest(_today, Guid.NewGuid()));
    await _memberships.AnnulAsync(_adminId, membership.Id, new MembershipAnnulRequest("Retracto del cliente"));

    var view = await _mine.GetMineByIdAsync(user, membership.Id);

    Assert.Equal("Annulled", view.DisplayStatus);
    Assert.Equal(["Blocked", "Delivered", "NotDeliverable"], view.Gifts.Select(gift => gift.Status));
  }

  [Fact]
  public async Task AfterAMerge_TheUserSeesTheMovedMemberships_WithTheOriginalHolder()
  {
    var user = await CreateUserAsync();
    var plan = await CreatePlanAsync();
    // The account was linked to the duplicate profile; the surviving profile had none.
    var duplicate = await CreateCustomerAsync("Carlos P.", linkedUserId: user);
    var surviving = await CreateCustomerAsync("Carlos Pérez");
    var moved = await SellAsync(duplicate.Id, plan, activate: true);
    var own = await SellAsync(surviving.Id, plan, activate: true);
    await Consume(moved.Id, _haircut.Id, 1);

    await _customers.MergeAsync(_adminId, surviving.Id, new CustomerMergeRequest(duplicate.Id));

    Assert.Equal(new[] { moved.Id, own.Id }.Order(), (await _mine.GetMineAsync(user)).Select(summary => summary.Id).Order());
    var movedView = await _mine.GetMineByIdAsync(user, moved.Id);
    Assert.Equal("Carlos P.", movedView.PurchasedByName);
    Assert.Single(movedView.Consumptions);
    Assert.Null((await _mine.GetMineByIdAsync(user, own.Id)).PurchasedByName);
  }

  [Fact]
  public async Task LaterCatalogChanges_DoNotAlterWhatTheCustomerBought()
  {
    var user = await CreateUserAsync();
    var customer = await CreateCustomerAsync(linkedUserId: user);
    var plan = await CreatePlanAsync();
    var membership = await SellAsync(customer.Id, plan, activate: true);
    var before = await _mine.GetMineByIdAsync(user, membership.Id);

    await _plans.UpdateAsync(plan.Id, new MembershipPlanUpsertRequest(
        "Oro renovada", "Otra", 300000, "PerService", null, 15, "Términos nuevos", "Otras", [new(_haircut.Id, 1)], []));
    await _plans.UpdateStatusAsync(plan.Id, new MembershipPlanStatusUpdateRequest(false));
    var after = await _mine.GetMineByIdAsync(user, membership.Id);

    Assert.Equal((before.PlanName, before.PlanDescription, before.Price, before.Mode, before.Terms, before.SpecificConditions),
        (after.PlanName, after.PlanDescription, after.Price, after.Mode, after.Terms, after.SpecificConditions));
    Assert.Equal(before.Balances.Select(line => string.Join(",", line.Services)), after.Balances.Select(line => string.Join(",", line.Services)));
    Assert.Equal(before.Gifts, after.Gifts);
  }

  private Task<CustomerMembershipDetailView> Consume(Guid membershipId, Guid serviceId, int quantity)
      => _ledger.ConsumeAsync(_adminId, membershipId, new MembershipConsumptionRequest(serviceId, quantity, _today, null, Guid.NewGuid()));

  private async Task<Guid> CreateUserAsync()
  {
    var user = new User("Cliente App", $"{Guid.NewGuid():N}@example.com", "hash", DateTime.UtcNow);
    _dbContext.Users.Add(user);
    await _dbContext.SaveChangesAsync();
    return user.Id;
  }

  private async Task<CustomerDetailView> CreateCustomerAsync(string name = "Carlos Pérez", Guid? linkedUserId = null)
  {
    var customer = await _customers.CreateAsync(_adminId, new CustomerCreateRequest(name, "3001234567", $"{Guid.NewGuid():N}@example.com"));
    if (linkedUserId is { } userId)
    {
      var profile = await _dbContext.Customers.SingleAsync(candidate => candidate.Id == customer.Id);
      profile.LinkAccount(userId, _adminId, DateTime.UtcNow);
      await _dbContext.SaveChangesAsync();
    }

    return customer;
  }

  private async Task<CustomerMembershipDetailView> SellAsync(
      Guid customerId, MembershipPlanView plan, bool activate, DateOnly? startsOn = null, DateOnly? endsOn = null)
  {
    var expired = activate && endsOn < _today;
    return await _memberships.SellAsync(_adminId, customerId, new MembershipSaleRequest(
        plan.Id, startsOn ?? _today, endsOn, ActivateNow: activate,
        ConfirmExpiredActivation: expired, ActivationReason: expired ? "Venta registrada tarde" : null));
  }

  private async Task<MembershipPlanView> CreatePlanAsync()
      => await _plans.CreateAsync(new MembershipPlanUpsertRequest(
          $"Oro {Guid.NewGuid():N}"[..12], "Diez servicios", 250000, "Pool", 10, 90, Terms, "Solo de lunes a jueves",
          [new(_haircut.Id, null), new(_beard.Id, null)],
          [
            new(null, "Service", _beard.Id, null, 2, "EveryPurchase"),
            new(null, "Physical", null, "Gorra", 1, "EveryPurchase"),
            new(null, "Physical", null, "Llavero", 1, "EveryPurchase"),
          ]));
}
