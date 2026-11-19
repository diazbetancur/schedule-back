using Barbershop.Application.Bills;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Finance.Admin;
using Barbershop.Domain.Bills;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.Products;
using Barbershop.Domain.Services;
using Barbershop.Infrastructure.Loyalty;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Features.Bills;

/// <summary>ADR-0020 stage 4: unapproving a bill reverses every posting; approving the corrected bill posts it again.</summary>
public sealed partial class BillsServiceTests
{
  [Fact]
  public async Task Unapprove_ReversesEveryPosting_AndTheCorrectedApprovalNetsLikeApprovingItFromScratch()
  {
    await EnableLoyaltyAsync();
    await ReceiveAsync(_wax.Id, 1, 20000);
    var customer = await CreateLinkedCustomerAsync();
    var bill = await AddServiceAsync(_barberUser, await OpenForCustomerAsync(_barberUser, customer));
    var serviceLine = bill.Lines[0].Id;
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(bill.Version, "Product", ProductId: _wax.Id, Quantity: 2));
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(bill.Version, "Tip", _barber.Id, Amount: 5000, TipPaymentMode: "WithBill"));
    bill = await _bills.AddLineAsync(_admin.Id, bill.Id, new BillLineRequest(bill.Version, "Discount", Amount: 6000, Reason: "Promoción", AppliesToLineId: serviceLine));
    bill = await ApproveAsync(_approverUser, await CloseAsync(_barberUser, bill));
    Assert.Equal(5, await _dbContext.BillPostings.CountAsync());

    _clock.Advance(TimeSpan.FromDays(1));
    var unapproved = await UnapproveAsync(_approverUser, bill, "El descuento no correspondía");

    Assert.Equal(("Unapproved", "El descuento no correspondía", 1), (unapproved.Status, unapproved.LastUnapprovalReason, unapproved.ApprovalCount));
    var incomes = await _dbContext.IncomeEntries.AsNoTracking().ToListAsync();
    var counters = incomes.Where(income => income.IsReversal).ToList();
    Assert.Equal(3, counters.Count);
    // Q8: each counter-entry mirrors its original (amount, base and commission) and is dated the day of the unapproval.
    foreach (var counter in counters)
    {
      var original = incomes.Single(income => income.Id == counter.ReversesIncomeEntryId);
      Assert.Equal((-original.Amount, -original.ProfessionalAmount, -original.BusinessAmount), (counter.Amount, counter.ProfessionalAmount, counter.BusinessAmount));
      Assert.Equal(Today, counter.OccurredOn);
      Assert.Equal(Today.AddDays(-1), original.OccurredOn);
    }

    Assert.Equal(0, incomes.Sum(income => income.Amount));
    Assert.Equal(0, incomes.Sum(income => income.ProfessionalAmount));
    // The units come back at the cost they left with; the average stays right.
    var stock = await _dbContext.ProductStocks.AsNoTracking().SingleAsync();
    Assert.Equal((1, 20000m), (stock.OnHand, stock.AverageUnitCost!.Value));
    var returned = await _dbContext.InventoryMovements.AsNoTracking().SingleAsync(movement => movement.Type == InventoryMovementType.SaleReversal);
    Assert.Equal((2, 20000m, 1), (returned.Quantity, returned.UnitCost!.Value, returned.OnHandAfter));
    // Q9: nothing was spent, so the points simply go; no debt.
    Assert.Equal(0, await AvailablePointsAsync(customer));
    Assert.Empty(await _dbContext.LoyaltyDebts.ToListAsync());
    Assert.All(await _dbContext.BillPostings.AsNoTracking().ToListAsync(), posting => Assert.NotNull(posting.ReversedAt));
    Assert.Contains(await _dbContext.UserNotifications.AsNoTracking().Select(notification => notification.Type).ToListAsync(), type => type == "loyalty.points_withdrawn");

    // The approver corrects it (the discount goes) and approves again.
    var discount = unapproved.Lines.Single(line => line.Type == "Discount");
    var corrected = await _bills.RemoveLineAsync(_approverUser.Id, bill.Id, discount.Id, unapproved.Version);
    var reapproved = await ApproveAsync(_approverUser, corrected);
    Assert.Equal(("Approved", 2), (reapproved.Status, reapproved.ApprovalCount));

    // Net: exactly the postings of the corrected bill approved from scratch (30.000 service, 70.000 products, 5.000 tip).
    var net = (await _dbContext.IncomeEntries.AsNoTracking().Where(income => income.BillId == bill.Id).ToListAsync());
    Assert.Equal((30000 + 70000 + 5000, 18000 + 5000, 12000 + 70000), (net.Sum(i => i.Amount), net.Sum(i => i.ProfessionalAmount), net.Sum(i => i.BusinessAmount)));
    // The money of the new approval is dated the day of the new approval: the period of the service never changes.
    var reversedIds = counters.Select(counter => counter.ReversesIncomeEntryId!.Value).ToHashSet();
    Assert.All(net.Where(income => !income.IsReversal && !reversedIds.Contains(income.Id)), income => Assert.Equal(Today, income.OccurredOn));
    Assert.Equal(-1, (await _dbContext.ProductStocks.AsNoTracking().SingleAsync()).OnHand);
    Assert.Equal(10, await AvailablePointsAsync(customer));
  }

  [Fact]
  public async Task ApproveRedeemUnapproveApprove_NeverGivesPoints_TheSpentPartIsADebtSettledByTheNewApproval()
  {
    await EnableLoyaltyAsync();
    var customer = await CreateLinkedCustomerAsync();
    var bill = await ApproveAsync(_approverUser, await CloseAsync(_barberUser, await AddServiceAsync(_barberUser, await OpenForCustomerAsync(_barberUser, customer))));
    Assert.Equal(10, await AvailablePointsAsync(customer));

    // The customer spends the 10 points on a reward (reserved, then delivered at the counter).
    var reward = await _redemptions.CreateAsync(_admin.Id, new LoyaltyRewardUpsertRequestFactory().Other("Gorra", 10));
    var redemption = await _redemptions.CreateAtCounterAsync(
        _approverUser.Id, customer, new Barbershop.Application.Loyalty.Redemptions.LoyaltyCounterRedemptionRequest(reward.Id, true, true, Guid.NewGuid()));
    await _redemptions.DeliverAsync(_approverUser.Id, redemption.Id,
        new Barbershop.Application.Loyalty.Redemptions.LoyaltyRedemptionDecisionRequest((await _redemptions.GetAsync(redemption.Id)).Version));
    Assert.Equal(0, await AvailablePointsAsync(customer));

    await UnapproveAsync(_approverUser, bill, "Se registró al cliente equivocado");
    var debt = await _dbContext.LoyaltyDebts.AsNoTracking().SingleAsync();
    Assert.Equal((LoyaltyDebtOrigin.PointsReversal, 10, 10), (debt.Origin, debt.Amount, debt.OutstandingPoints));
    Assert.Equal(-10, await AvailablePointsAsync(customer));

    await ApproveAsync(_approverUser, await _bills.GetAsync(_approverUser.Id, bill.Id));

    // The new lot settles the debt: the customer ends with what one approval gives minus what they spent.
    Assert.Equal(0, await AvailablePointsAsync(customer));
    Assert.Equal(0, (await _dbContext.LoyaltyDebts.AsNoTracking().SingleAsync()).OutstandingPoints);
  }

  [Fact]
  public async Task AnUnapprovalIsIdempotent_ASecondOneIsAConflict_AndAnUnapprovedBillCanBeVoided()
  {
    var bill = await ApproveAsync(_approverUser, await CloseAsync(_barberUser, await AddServiceAsync(_barberUser, await OpenWalkInAsync(_barberUser, "Cliente"))));
    var request = new BillUnapproveRequest(bill.Version, Guid.NewGuid(), "Error de registro");

    var first = await _bills.UnapproveAsync(_approverUser.Id, bill.Id, request);
    var replay = await _bills.UnapproveAsync(_approverUser.Id, bill.Id, request);

    Assert.Equal(("Unapproved", "Unapproved"), (first.Status, replay.Status));
    Assert.Single(await _dbContext.IncomeEntries.Where(income => income.ReversesIncomeEntryId != null).ToListAsync());
    await Assert.ThrowsAsync<ConflictException>(() => _bills.UnapproveAsync(_admin.Id, bill.Id, new BillUnapproveRequest(replay.Version, Guid.NewGuid(), "Otra vez")));
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _bills.UnapproveAsync(_barberUser.Id, bill.Id, new BillUnapproveRequest(replay.Version, Guid.NewGuid(), "Sin permiso")));

    // ADR-0020 §2: Desaprobada → Anulada (no postings left), keeping its number.
    var voided = await _bills.VoidAsync(_approverUser.Id, bill.Id, new BillReasonRequest(replay.Version, "No se prestó el servicio"));
    Assert.Equal(("Voided", "C-000001"), (voided.Status, voided.Number));
    Assert.Equal(0, await _dbContext.IncomeEntries.SumAsync(income => income.Amount));
  }

  [Fact]
  public async Task TheSelfApprovalRule_AppliesToTheUnapprovalToo()
  {
    var approverStaff = new Barbershop.Domain.Staff.StaffProfile(_approverUser.Id, "Ana", 30, DateTime.UtcNow);
    _dbContext.StaffProfiles.Add(approverStaff);
    await _dbContext.SaveChangesAsync();
    var bill = await OpenWalkInAsync(_barberUser, "Cliente");
    // ADR-0021 L1: the line on another professional's name is put by the Admin.
    bill = await _bills.AddLineAsync(_admin.Id, bill.Id, new BillLineRequest(bill.Version, "Service", approverStaff.Id, ServiceCatalogDefaults.CorteGeneralServiceId, _haircut.Id));
    bill = await ApproveAsync(_admin, await CloseAsync(_barberUser, bill));

    var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => UnapproveAsync(_approverUser, bill, "Me equivoqué"));
    Assert.Equal(Barbershop.Infrastructure.Bills.BillsService.SelfUnapprovalForbiddenMessage, refused.Message);

    // An Admin who is the customer may, and the unapproval says so.
    var adminProfile = new CustomerProfile("Admin Cliente", "3009998877", "admin-cliente@example.com", _admin.Id, DateTime.UtcNow);
    adminProfile.LinkAccount(_admin.Id, _admin.Id, DateTime.UtcNow);
    _dbContext.Customers.Add(adminProfile);
    await _dbContext.SaveChangesAsync();
    var own = await ApproveAsync(_approverUser, await CloseAsync(_barberUser, await AddServiceAsync(_barberUser, await OpenForCustomerAsync(_barberUser, adminProfile.Id))));
    await UnapproveAsync(_admin, own, "Corrección");
    Assert.True((await _dbContext.BillApprovals.AsNoTracking().SingleAsync(approval => approval.BillId == own.Id)).IsSelfUnapproved);
  }

  [Fact]
  public async Task AMembershipExpiredAfterTheApproval_GetsItsUseBack_WithoutBeingReactivated()
  {
    var customer = await CreateLinkedCustomerAsync();
    var membership = await SellMembershipAsync(customer, pool: 1);
    var bill = await OpenForCustomerAsync(_barberUser, customer);
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(
        bill.Version, "MembershipConsumption", _barber.Id, ServiceId: _haircut.Id, CustomerMembershipId: membership));
    bill = await ApproveAsync(_approverUser, await CloseAsync(_barberUser, bill));
    var sold = await _dbContext.CustomerMemberships.AsNoTracking().SingleAsync(candidate => candidate.Id == membership);

    _clock.Advance(TimeSpan.FromDays(sold.EndsOn.DayNumber - Today.DayNumber + 2));
    await UnapproveAsync(_approverUser, bill, "El cliente no usó la membresía");

    var reversal = await _dbContext.MembershipConsumptionReversals.AsNoTracking().SingleAsync();
    Assert.Contains("desaprobada", reversal.Reason);
    Assert.Equal(sold.Status, (await _dbContext.CustomerMemberships.AsNoTracking().SingleAsync(candidate => candidate.Id == membership)).Status);
    // The consumption of a bill is not reversed from the membership itself.
    var consumption = await _dbContext.MembershipConsumptions.AsNoTracking().SingleAsync();
    Assert.Equal(consumption.Id, reversal.MembershipConsumptionId);
  }

  [Fact]
  public async Task UnapprovingWithTheStockAlreadyNegative_GivesTheUnitsBack_AndWithoutCostLeavesTheAverageAlone()
  {
    var bill = await OpenWalkInAsync(_barberUser, "Cliente");
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(bill.Version, "Product", ProductId: _wax.Id, Quantity: 2));
    bill = await ApproveAsync(_approverUser, await CloseAsync(_barberUser, bill));
    Assert.Equal(-2, (await _dbContext.ProductStocks.AsNoTracking().SingleAsync()).OnHand);

    await UnapproveAsync(_approverUser, bill, "Se devolvió el producto");

    var stock = await _dbContext.ProductStocks.AsNoTracking().SingleAsync();
    Assert.Equal((0, (decimal?)null), (stock.OnHand, stock.AverageUnitCost));
    var product = await _dbContext.IncomeEntries.AsNoTracking().SingleAsync(income => income.ReversesIncomeEntryId != null);
    Assert.Equal((-70000, (decimal?)null), (product.Amount, product.UnitCostSnapshot));
  }

  [Fact]
  public void AReturnedSale_CountsInTheWeightedAverageAtItsOwnCost()
  {
    var stock = new ProductStock(_wax.Id, DateTime.UtcNow);
    stock.Receive(4, 10000, Today, null, null, null, Guid.NewGuid(), _admin.Id, DateTime.UtcNow);
    stock.Receive(4, 20000, Today, null, null, null, Guid.NewGuid(), _admin.Id, DateTime.UtcNow);
    var sale = stock.Sell(2, Today, Guid.NewGuid(), _admin.Id, DateTime.UtcNow);
    stock.Receive(6, 30000, Today, null, null, null, Guid.NewGuid(), _admin.Id, DateTime.UtcNow);

    stock.ReturnSale(2, sale.UnitCost, Today, Guid.NewGuid(), _admin.Id, DateTime.UtcNow);

    // 6 × 15.000 + 6 × 30.000 = 270.000 for 12, then 2 back at 15.000: 300.000 / 14.
    Assert.Equal(14, stock.OnHand);
    Assert.Equal(Math.Round(300000m / 14, 4), stock.AverageUnitCost);
  }

  [Fact]
  public async Task Q20_ARedemptionDeliveredByTheBill_GoesBackToApprovedWithItsPointsReserved_AndNoDeadline()
  {
    var customer = await CreateLinkedCustomerAsync();
    var redemptionId = await ApprovedServiceRedemptionAsync(customer);
    var bill = await OpenForCustomerAsync(_barberUser, customer);
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(bill.Version, "Redemption", _barber.Id, LoyaltyRedemptionId: redemptionId));
    bill = await ApproveAsync(_admin, await CloseAsync(_barberUser, bill));
    var available = await AvailablePointsAsync(customer);

    await UnapproveAsync(_admin, bill, "El servicio no se prestó");

    var redemption = await _dbContext.LoyaltyRedemptions.AsNoTracking().SingleAsync(candidate => candidate.Id == redemptionId);
    Assert.Equal((LoyaltyRedemptionStatus.Approved, (Guid?)null), (redemption.Status, redemption.BillLineId));
    Assert.NotNull(redemption.ReopenedAt);
    // Reserved again, not freed: the free balance does not change.
    Assert.Equal(available, await AvailablePointsAsync(customer));
    var restored = await _dbContext.LoyaltyLots.AsNoTracking().SingleAsync(lot => lot.SourceType == LoyaltySourceType.RedemptionRestore);
    Assert.Equal((50, 0, 50), (restored.InitialPoints, restored.RemainingPoints, restored.ReservedPoints));

    // Past its old deadline it neither expires nor blocks the corrected bill.
    _clock.Advance(TimeSpan.FromDays(400));
    Assert.Equal(0, await _redemptions.ExpireDueRedemptionsAsync(CancellationToken.None));
    await ApproveAsync(_admin, await _bills.GetAsync(_admin.Id, bill.Id));
    var delivered = await _dbContext.LoyaltyRedemptions.AsNoTracking().SingleAsync(candidate => candidate.Id == redemptionId);
    Assert.Equal(LoyaltyRedemptionStatus.Delivered, delivered.Status);
    Assert.Equal(0, (await _dbContext.LoyaltyLots.AsNoTracking().SingleAsync(lot => lot.Id == restored.Id)).ReservedPoints);
  }

  [Fact]
  public async Task ACustomerMergedAfterTheApproval_IsCorrectedInTheLiveProfile()
  {
    await EnableLoyaltyAsync();
    var origin = await CreateLinkedCustomerAsync();
    var bill = await ApproveAsync(_approverUser, await CloseAsync(_barberUser, await AddServiceAsync(_barberUser, await OpenForCustomerAsync(_barberUser, origin))));
    var live = new CustomerProfile("Carla Unificada", "3005556677", "carla2@example.com", _admin.Id, DateTime.UtcNow);
    _dbContext.Customers.Add(live);
    await _dbContext.SaveChangesAsync();
    // The merge moves the 10 points to the live profile (as the merge service does).
    var lot = await _dbContext.LoyaltyLots.SingleAsync();
    lot.Take(10);
    var moved = LoyaltyLot.TransferredFrom(lot, live.Id, 10, DateTime.UtcNow);
    _dbContext.LoyaltyLots.Add(moved);
    _dbContext.LoyaltyMovements.Add(new LoyaltyMovement(origin, LoyaltyMovementType.MergeTransferOut, 10, DateTime.UtcNow, lotId: lot.Id));
    _dbContext.LoyaltyMovements.Add(new LoyaltyMovement(live.Id, LoyaltyMovementType.MergeTransferIn, 10, DateTime.UtcNow, lotId: moved.Id));
    (await _dbContext.Customers.SingleAsync(customer => customer.Id == origin)).MarkMergedInto(live.Id, _admin.Id, DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();
    _dbContext.ChangeTracker.Clear();

    await UnapproveAsync(_approverUser, bill, "Cliente equivocado");

    Assert.Equal(0, (await _dbContext.LoyaltyLots.AsNoTracking().SingleAsync(candidate => candidate.Id == moved.Id)).RemainingPoints);
    Assert.Empty(await _dbContext.LoyaltyDebts.ToListAsync());
    Assert.Equal(0, await AvailablePointsAsync(live.Id));
  }

  [Fact]
  public async Task Reports_NetTheCounterEntries_AndOlderPeriodsDoNotChange()
  {
    await ReceiveAsync(_wax.Id, 3, 20000);
    var bill = await OpenWalkInAsync(_barberUser, "Cliente");
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(bill.Version, "Service", _barber.Id, ServiceCatalogDefaults.CorteGeneralServiceId, _haircut.Id));
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(bill.Version, "Product", ProductId: _wax.Id, Quantity: 1));
    bill = await ApproveAsync(_approverUser, await CloseAsync(_barberUser, bill));
    var serviceDay = Today;
    var reports = new Barbershop.Infrastructure.Finance.ReportsQueryService(_dbContext, _clock);
    var before = await reports.GetSummaryAsync(serviceDay, serviceDay);

    _clock.Advance(TimeSpan.FromDays(2));
    await UnapproveAsync(_approverUser, bill, "Corrección");

    Assert.Equal(before.TotalIncome, (await reports.GetSummaryAsync(serviceDay, serviceDay)).TotalIncome);
    var correction = await reports.GetSummaryAsync(Today, Today);
    Assert.Equal((-65000L, -1, -1, -18000L), (correction.TotalIncome, correction.IncomeCount, correction.ProductUnitsSold, correction.ProfessionalIncome));
    var whole = await reports.GetSummaryAsync(serviceDay, Today);
    Assert.Equal((0L, 0, 0, 0L), (whole.TotalIncome, whole.IncomeCount, whole.ProductUnitsSold, whole.ProfessionalIncome));

    var inventory = new Barbershop.Infrastructure.Products.InventoryManagementService(_dbContext, _clock);
    var sales = await inventory.GetSalesAsync(new Barbershop.Application.Products.InventorySalesQuery(serviceDay, Today));
    var row = Assert.Single(sales.Items);
    Assert.Equal((0, 0L, 0L, 0L), (row.Units, row.Revenue, row.Cost, row.Profit));
  }

  private async Task<BillView> UnapproveAsync(Barbershop.Domain.Users.User user, BillView bill, string reason)
  {
    var current = await _bills.GetAsync(user.Id, bill.Id);
    return await _bills.UnapproveAsync(user.Id, bill.Id, new BillUnapproveRequest(current.Version, Guid.NewGuid(), reason));
  }

  private Task<int> AvailablePointsAsync(Guid customerId)
      => new LoyaltyWalletService(_dbContext, _clock).AvailablePointsAsync(customerId, _clock.GetUtcNow().UtcDateTime, CancellationToken.None);

  private sealed class LoyaltyRewardUpsertRequestFactory
  {
    public Barbershop.Application.Loyalty.Redemptions.LoyaltyRewardUpsertRequest Other(string name, int cost)
        => new(name, null, "Other", null, cost);
  }
}
