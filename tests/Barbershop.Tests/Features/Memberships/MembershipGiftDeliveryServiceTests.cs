using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Domain.Common;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Services;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Customers;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Barbershop.Tests.Features.Memberships;

public sealed class MembershipGiftDeliveryServiceTests : IDisposable
{
  private const string Terms = "La membresía es personal e intransferible.";

  private readonly DateOnly _today = DateOnly.FromDateTime(BogotaClock.ToLocal(DateTime.UtcNow));
  private readonly AppDbContext _dbContext;
  private readonly IAdminMembershipsService _memberships;
  private readonly IAdminMembershipLedgerService _ledger;
  private readonly IAdminMembershipGiftDeliveryService _deliveries;
  private readonly IAdminMembershipPlansService _plans;
  private readonly IAdminCustomersService _customers;
  private readonly Guid _adminId;
  private readonly Service _haircut;
  private readonly Service _beard;

  public MembershipGiftDeliveryServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        // The in-memory provider has no transactions; deliveries open one for PostgreSQL.
        .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options;

    _dbContext = new AppDbContext(options);
    _memberships = new MembershipSalesService(_dbContext, TimeProvider.System);
    _ledger = new MembershipLedgerService(_dbContext, _memberships, TimeProvider.System);
    _deliveries = new MembershipGiftDeliveryService(_dbContext, _memberships, TimeProvider.System);
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
  public async Task Delivery_HandsOverTheWholeGift_AndKeepsDateAndAdmin()
  {
    var membership = await SellActiveAsync(soldDaysAgo: 5);
    var cap = Gift(membership, "Gorra");

    var delivered = await DeliverAsync(membership.Id, cap.Id, _today.AddDays(-2));

    var gift = Gift(delivered, "Gorra");
    Assert.Equal("Delivered", gift.Status);
    Assert.Equal(_today.AddDays(-2), gift.DeliveredOn);
    var delivery = Assert.Single(delivered.GiftDeliveries);
    Assert.Equal(gift.DeliveryId, delivery.Id);
    Assert.Equal(cap.Id, delivery.GiftId);
    Assert.Equal(3, delivery.Quantity); // the whole quantity, never a part of it
    Assert.Equal("Gorra", delivery.GiftDescription);
    Assert.Equal("Admin Principal", delivery.RegisteredByName);
    Assert.Null(delivery.Reversal);
    Assert.Equal("PendingDelivery", Gift(delivered, "Llavero").Status);
  }

  [Fact]
  public async Task SecondDelivery_WithoutAReversal_IsRejected()
  {
    var membership = await SellActiveAsync();
    var cap = Gift(membership, "Gorra");
    await DeliverAsync(membership.Id, cap.Id);

    await Assert.ThrowsAsync<ConflictException>(() => DeliverAsync(membership.Id, cap.Id));

    Assert.Equal(1, await _dbContext.MembershipGiftDeliveries.CountAsync());
  }

  [Fact]
  public async Task Reversal_NeedsAReason_HappensOnce_AndAllowsANewDelivery()
  {
    var membership = await SellActiveAsync();
    var cap = Gift(membership, "Gorra");
    var first = Gift(await DeliverAsync(membership.Id, cap.Id), "Gorra").DeliveryId!.Value;

    var noReason = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _deliveries.ReverseAsync(_adminId, membership.Id, cap.Id, first, new MembershipGiftDeliveryReverseRequest("  ")));
    Assert.Contains("reason", noReason.Errors.Keys);

    var reversed = await _deliveries.ReverseAsync(_adminId, membership.Id, cap.Id, first, new MembershipGiftDeliveryReverseRequest("Se entregó al cliente equivocado"));
    Assert.Equal("PendingDelivery", Gift(reversed, "Gorra").Status);
    Assert.Null(Gift(reversed, "Gorra").DeliveryId);
    var reversal = Assert.Single(reversed.GiftDeliveries).Reversal!;
    Assert.Equal("Se entregó al cliente equivocado", reversal.Reason);
    Assert.Equal("Admin Principal", reversal.PerformedByName);

    await Assert.ThrowsAsync<ConflictException>(() =>
        _deliveries.ReverseAsync(_adminId, membership.Id, cap.Id, first, new MembershipGiftDeliveryReverseRequest("Otra vez")));

    var again = await DeliverAsync(membership.Id, cap.Id);
    Assert.Equal("Delivered", Gift(again, "Gorra").Status);
    Assert.NotEqual(first, Gift(again, "Gorra").DeliveryId);
    // Both deliveries stay in the history: the reversed one with its reason, the new one current.
    Assert.Equal(2, again.GiftDeliveries.Count);
    Assert.Single(again.GiftDeliveries, delivery => delivery.Reversal is null);
    Assert.Equal(1, await _dbContext.MembershipGiftDeliveryReversals.CountAsync());
  }

  [Fact]
  public async Task Annulment_KeepsDeliveredGiftsDelivered_AndBlocksPendingOnes_WhateverTheDate()
  {
    var membership = await SellActiveAsync(soldDaysAgo: 10);
    var cap = Gift(membership, "Gorra");
    var keyring = Gift(membership, "Llavero");
    await DeliverAsync(membership.Id, cap.Id, _today.AddDays(-8));

    var annulled = await _memberships.AnnulAsync(_adminId, membership.Id, new MembershipAnnulRequest("Retracto del cliente"));

    Assert.Equal("Delivered", Gift(annulled, "Gorra").Status);
    Assert.Equal(_today.AddDays(-8), Gift(annulled, "Gorra").DeliveredOn);
    Assert.Equal("NotDeliverable", Gift(annulled, "Llavero").Status);
    // Handed over while it was still active? It cannot be registered after the annulment.
    await Assert.ThrowsAsync<ConflictException>(() => DeliverAsync(membership.Id, keyring.Id, _today.AddDays(-9)));
    await Assert.ThrowsAsync<ConflictException>(() => DeliverAsync(membership.Id, keyring.Id, _today));
  }

  [Fact]
  public async Task ReversingADeliveryAfterTheAnnulment_IsAllowed_ButTheGiftStaysNotDeliverable()
  {
    var membership = await SellActiveAsync();
    var cap = Gift(membership, "Gorra");
    var deliveryId = Gift(await DeliverAsync(membership.Id, cap.Id), "Gorra").DeliveryId!.Value;
    await _memberships.AnnulAsync(_adminId, membership.Id, new MembershipAnnulRequest("Retracto del cliente"));

    var reversed = await _deliveries.ReverseAsync(_adminId, membership.Id, cap.Id, deliveryId, new MembershipGiftDeliveryReverseRequest("La gorra fue devuelta"));

    Assert.Equal("Annulled", reversed.Status);
    Assert.Equal("NotDeliverable", Gift(reversed, "Gorra").Status);
    Assert.Equal("La gorra fue devuelta", Assert.Single(reversed.GiftDeliveries).Reversal!.Reason);
    await Assert.ThrowsAsync<ConflictException>(() => DeliverAsync(membership.Id, cap.Id));
  }

  [Fact]
  public async Task OnlyPhysicalGiftsOfAnActiveMembership_CanBeDelivered_OnAValidDate()
  {
    var membership = await SellActiveAsync(soldDaysAgo: 3);
    var cap = Gift(membership, "Gorra");
    var beardGift = membership.Gifts.Single(gift => gift.Kind == "Service");

    await Assert.ThrowsAsync<ConflictException>(() => DeliverAsync(membership.Id, beardGift.Id));
    await Assert.ThrowsAsync<NotFoundException>(() => DeliverAsync(membership.Id, Guid.NewGuid()));
    var other = await SellActiveAsync();
    await Assert.ThrowsAsync<NotFoundException>(() => DeliverAsync(other.Id, cap.Id));

    var future = await Assert.ThrowsAsync<ValidationProblemException>(() => DeliverAsync(membership.Id, cap.Id, _today.AddDays(1)));
    Assert.Contains("futura", future.Errors["deliveredOn"][0]);
    var beforeSale = await Assert.ThrowsAsync<ValidationProblemException>(() => DeliverAsync(membership.Id, cap.Id, _today.AddDays(-4)));
    Assert.Contains("anterior a la venta", beforeSale.Errors["deliveredOn"][0]);
    var noKey = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _deliveries.DeliverAsync(_adminId, membership.Id, cap.Id, new MembershipGiftDeliveryRequest(_today, Guid.Empty)));
    Assert.Contains("idempotencyKey", noKey.Errors.Keys);

    var plan = await CreatePlanAsync();
    var customer = await CreateCustomerAsync();
    var pending = await _memberships.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today));
    await Assert.ThrowsAsync<ConflictException>(() => DeliverAsync(pending.Id, Gift(pending, "Gorra").Id));

    // An expired membership still hands over a gift it was granted.
    var expired = await SellActiveAsync(soldDaysAgo: 60, endsDaysAgo: 30);
    Assert.Equal("Delivered", Gift(await DeliverAsync(expired.Id, Gift(expired, "Gorra").Id), "Gorra").Status);

    Assert.Equal(1, await _dbContext.MembershipGiftDeliveries.CountAsync());
  }

  [Fact]
  public async Task RetriedRequest_WithTheSameKey_IsStoredOnce()
  {
    var membership = await SellActiveAsync();
    var cap = Gift(membership, "Gorra");
    var request = new MembershipGiftDeliveryRequest(_today, Guid.NewGuid());

    var first = await _deliveries.DeliverAsync(_adminId, membership.Id, cap.Id, request);
    var retried = await _deliveries.DeliverAsync(_adminId, membership.Id, cap.Id, request);

    Assert.Equal(Gift(first, "Gorra").DeliveryId, Gift(retried, "Gorra").DeliveryId);
    Assert.Equal(1, await _dbContext.MembershipGiftDeliveries.CountAsync());
    await Assert.ThrowsAsync<ConflictException>(() =>
        _deliveries.DeliverAsync(_adminId, membership.Id, cap.Id, request with { DeliveredOn = _today.AddDays(-1) }));
    await Assert.ThrowsAsync<ConflictException>(() =>
        _deliveries.DeliverAsync(_adminId, membership.Id, Gift(membership, "Llavero").Id, request));
    Assert.Equal(1, await _dbContext.MembershipGiftDeliveries.CountAsync());
  }

  [Fact]
  public async Task Delivering_TouchesNoFinanceBalancesOrSaleConditions_AndSurvivesPlanChanges()
  {
    var plan = await CreatePlanAsync();
    var customer = await CreateCustomerAsync();
    var membership = await _memberships.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(plan.Id, _today, ActivateNow: true));
    var income = IncomeEntry.CreateMembershipSale(250000, _today, _adminId, DateTime.UtcNow);
    _dbContext.IncomeEntries.Add(income);
    await _dbContext.SaveChangesAsync();
    await _memberships.LinkIncomeAsync(_adminId, membership.Id, new MembershipIncomeLinkRequest(income.Id));
    await _ledger.ConsumeAsync(_adminId, membership.Id, new MembershipConsumptionRequest(_beard.Id, 2, _today, null, Guid.NewGuid()));
    var before = await _memberships.GetByIdAsync(membership.Id);
    var incomesBefore = await IncomesAsync();
    var appointmentsBefore = await _dbContext.Appointments.CountAsync();
    var cap = Gift(before, "Gorra");

    var delivered = await DeliverAsync(membership.Id, cap.Id);
    var reversed = await _deliveries.ReverseAsync(_adminId, membership.Id, cap.Id, Gift(delivered, "Gorra").DeliveryId!.Value, new MembershipGiftDeliveryReverseRequest("Prueba"));
    var after = await DeliverAsync(reversed.Id, cap.Id);

    Assert.Equal(incomesBefore, await IncomesAsync());
    Assert.Equal(appointmentsBefore, await _dbContext.Appointments.CountAsync());
    Assert.Equal(Lines(before), Lines(after));
    Assert.Equal(before.Consumptions.Single().Id, after.Consumptions.Single().Id);
    Assert.Equal((before.Price, before.Terms, before.Status, before.Income!.IncomeEntryId), (after.Price, after.Terms, after.Status, after.Income!.IncomeEntryId));
    Assert.Equal(before.Events.Count, after.Events.Count);

    // Editing the plan (dropping its gift rules) and deactivating it keeps the gifts and their deliveries.
    await _plans.UpdateAsync(plan.Id, new MembershipPlanUpsertRequest(
        "Oro renovada", null, 300000, "Pool", 5, 30, "Otros términos", null, [new(_haircut.Id, null)], []));
    await _plans.UpdateStatusAsync(plan.Id, new MembershipPlanStatusUpdateRequest(false));
    var reloaded = await _memberships.GetByIdAsync(membership.Id);
    Assert.Equal(after.Gifts, reloaded.Gifts);
    Assert.Equal(2, reloaded.GiftDeliveries.Count);
    Assert.Equal("Delivered", Gift(reloaded, "Gorra").Status);
  }

  [Fact]
  public async Task MergingCustomers_KeepsTheDeliveriesOfTheMovedMemberships()
  {
    var plan = await CreatePlanAsync();
    var source = await CreateCustomerAsync("Origen");
    var target = await CreateCustomerAsync("Destino");
    var membership = await _memberships.SellAsync(_adminId, source.Id, new MembershipSaleRequest(plan.Id, _today, ActivateNow: true));
    await DeliverAsync(membership.Id, Gift(membership, "Gorra").Id);

    await _customers.MergeAsync(_adminId, target.Id, new CustomerMergeRequest(source.Id));

    var moved = await _memberships.GetByIdAsync(membership.Id);
    Assert.Equal(target.Id, moved.CustomerId);
    Assert.Equal("Delivered", Gift(moved, "Gorra").Status);
    Assert.Single(moved.GiftDeliveries);
  }

  private static MembershipGiftView Gift(CustomerMembershipDetailView membership, string description)
      => membership.Gifts.Single(gift => gift.Description == description);

  private static IEnumerable<string> Lines(CustomerMembershipDetailView membership)
      => membership.Allowances.Select(line => $"{line.Origin}:{line.AvailableQuantity}/{line.GrantedQuantity}");

  private async Task<IReadOnlyList<string>> IncomesAsync()
      => await _dbContext.IncomeEntries.AsNoTracking()
          .Select(entry => $"{entry.Id}|{entry.Kind}|{entry.Amount}|{entry.StaffProfileId}|{entry.IsDeleted}|{entry.UpdatedAt:O}")
          .ToListAsync();

  private Task<CustomerMembershipDetailView> DeliverAsync(Guid membershipId, Guid giftId, DateOnly? deliveredOn = null)
      => _deliveries.DeliverAsync(_adminId, membershipId, giftId, new MembershipGiftDeliveryRequest(deliveredOn ?? _today, Guid.NewGuid()));

  private async Task<CustomerMembershipDetailView> SellActiveAsync(int soldDaysAgo = 0, int? endsDaysAgo = null)
  {
    var plan = await CreatePlanAsync();
    var customer = await CreateCustomerAsync();
    var startsOn = _today.AddDays(-soldDaysAgo);
    DateOnly? endsOn = endsDaysAgo is { } days ? _today.AddDays(-days) : null;
    var expired = endsOn < _today;
    return await _memberships.SellAsync(_adminId, customer.Id, new MembershipSaleRequest(
        plan.Id, startsOn, endsOn, SoldOn: startsOn, ActivateNow: true,
        ConfirmExpiredActivation: expired, ActivationReason: expired ? "Venta registrada tarde" : null));
  }

  private async Task<CustomerDetailView> CreateCustomerAsync(string name = "Carlos Pérez")
      => await _customers.CreateAsync(_adminId, new CustomerCreateRequest(name, "3001234567", $"{Guid.NewGuid():N}@example.com"));

  /// <summary>A pool of 10 with three gifts: 1 gifted beard, 3 caps and 1 keyring.</summary>
  private async Task<MembershipPlanView> CreatePlanAsync()
      => await _plans.CreateAsync(new MembershipPlanUpsertRequest(
          $"Oro {Guid.NewGuid():N}"[..12], null, 250000, "Pool", 10, 90, Terms, null,
          [new(_haircut.Id, null), new(_beard.Id, null)],
          [
            new(null, "Service", _beard.Id, null, 1, "EveryPurchase"),
            new(null, "Physical", null, "Gorra", 3, "EveryPurchase"),
            new(null, "Physical", null, "Llavero", 1, "EveryPurchase"),
          ]));
}
