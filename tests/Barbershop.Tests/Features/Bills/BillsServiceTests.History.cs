using Barbershop.Application.Bills;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Products;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Products;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Features.Bills;

/// <summary>ADR-0020 stage 5: the customer's receipts, the admin filters, the cash closing and the product rotation.</summary>
public sealed partial class BillsServiceTests
{
  [Fact]
  public async Task TheCustomer_SeesTheirReceipts_WithTheirMergedProfile_WithoutInternalStates_AndNeverSomeoneElses()
  {
    var origin = await CreateLinkedCustomerAsync();
    var first = await CloseAsync(_barberUser, await AddServiceAsync(_barberUser, await OpenForCustomerAsync(_barberUser, origin)));
    // An open bill has no number yet: it is not a receipt.
    await AddServiceAsync(_barberUser, await OpenForCustomerAsync(_barberUser, origin));
    // Someone else's receipt.
    var stranger = await CloseAsync(_barberUser, await AddServiceAsync(_barberUser, await OpenWalkInAsync(_barberUser, "Otro")));

    // The profile is merged into a new one linked to the same account (ADR-0013): the old receipts stay visible.
    var live = new CustomerProfile("Carla Unificada", "3005556677", "carla2@example.com", _admin.Id, DateTime.UtcNow);
    _dbContext.Customers.Add(live);
    var originProfile = await _dbContext.Customers.SingleAsync(customer => customer.Id == origin);
    originProfile.UnlinkAccount(DateTime.UtcNow);
    originProfile.MarkMergedInto(live.Id, _admin.Id, DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();
    live.LinkAccount(_customerUser.Id, _admin.Id, DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();
    _clock.Advance(TimeSpan.FromMinutes(5));
    var second = await ApproveAsync(_approverUser, await CloseAsync(_barberUser, await AddServiceAsync(_barberUser, await OpenForCustomerAsync(_barberUser, live.Id))));

    var mine = await _bills.GetMineAsync(_customerUser.Id, new MyBillListQuery(null, null));
    Assert.Equal([second.Id, first.Id], mine.Items.Select(bill => bill.Id));
    Assert.All(mine.Items, bill => Assert.Equal("Issued", bill.Status));
    Assert.Equal(("C-000003", 30000, "Efectivo"), (mine.Items[0].Number, mine.Items[0].Total, mine.Items[0].PaymentMethodName));

    var detail = await _bills.GetMineByIdAsync(_customerUser.Id, first.Id);
    var line = Assert.Single(detail.Lines);
    Assert.Equal(("Service", 30000, "Alex"), (line.Type, line.Amount, line.StaffName));
    await Assert.ThrowsAsync<NotFoundException>(() => _bills.GetMineByIdAsync(_customerUser.Id, stranger.Id));
    await Assert.ThrowsAsync<NotFoundException>(() => _bills.GetMineByIdAsync(_barberUser.Id, first.Id));

    // Q23: being corrected shows «En revisión» with the current values; voided shows «Anulado».
    await UnapproveAsync(_approverUser, second, "Corrección");
    Assert.Equal("UnderReview", (await _bills.GetMineByIdAsync(_customerUser.Id, second.Id)).Status);
    var current = await _bills.GetAsync(_approverUser.Id, first.Id);
    await _bills.VoidAsync(_approverUser.Id, first.Id, new BillReasonRequest(current.Version, "No se prestó"));
    Assert.Equal("Voided", (await _bills.GetMineByIdAsync(_customerUser.Id, first.Id)).Status);

    // The notice of the purchase links to the receipt.
    var notice = await _dbContext.UserNotifications.FirstAsync(notification => notification.UserId == _customerUser.Id && notification.Type == "bill.closed");
    Assert.StartsWith("/customer/bills/", notice.Url);
  }

  [Fact]
  public async Task TheAdminList_FiltersByCustomerFamily_AndByPaymentMethod()
  {
    var origin = await CreateLinkedCustomerAsync();
    var own = await CloseAsync(_barberUser, await AddServiceAsync(_barberUser, await OpenForCustomerAsync(_barberUser, origin)));
    var other = await AddServiceAsync(_barberUser, await OpenWalkInAsync(_barberUser, "Otro"));
    var transfer = await _dbContext.PaymentMethods.Where(method => method.Id != _cash).Select(method => method.Id).FirstAsync();
    other = await _bills.CloseAsync(_barberUser.Id, other.Id, new BillCloseRequest(other.Version, transfer));
    var live = new CustomerProfile("Carla Unificada", "3005556677", "carla2@example.com", _admin.Id, DateTime.UtcNow);
    _dbContext.Customers.Add(live);
    (await _dbContext.Customers.SingleAsync(customer => customer.Id == origin)).MarkMergedInto(live.Id, _admin.Id, DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();

    var byCustomer = await _bills.ListAsync(_admin.Id, new BillListQuery(CustomerId: live.Id));
    Assert.Equal(own.Id, Assert.Single(byCustomer.Items).Id);
    var byMethod = await _bills.ListAsync(_admin.Id, new BillListQuery(PaymentMethodId: transfer));
    Assert.Equal(other.Id, Assert.Single(byMethod.Items).Id);
  }

  [Fact]
  public async Task TheCashClosing_GroupsWhatWasChargedByDayAndMethod_AndShowsTheVoidedApart()
  {
    var first = await CloseAsync(_barberUser, await AddServiceAsync(_barberUser, await OpenWalkInAsync(_barberUser, "Uno")));
    await CloseAsync(_barberUser, await AddServiceAsync(_barberUser, await OpenWalkInAsync(_barberUser, "Dos")));
    var voided = await CloseAsync(_barberUser, await AddServiceAsync(_barberUser, await OpenWalkInAsync(_barberUser, "Tres")));
    voided = await _bills.GetAsync(_approverUser.Id, voided.Id);
    await _bills.VoidAsync(_approverUser.Id, voided.Id, new BillReasonRequest(voided.Version, "Error"));
    // Unapproving does not change what was charged (the cash report is money received, not income).
    await UnapproveAsync(_approverUser, await ApproveAsync(_approverUser, first), "Corrección");

    var closing = await _bills.GetCashClosingAsync(new CashClosingQuery(Today, Today));
    var day = Assert.Single(closing.Days);
    Assert.Equal((Today, 2, 60000L), (day.Day, day.BillCount, day.Total));
    var method = Assert.Single(closing.Methods);
    Assert.Equal(("Efectivo", 2, 60000L), (method.Name, method.BillCount, method.Total));
    Assert.Equal((2, 60000L, 1, 30000L), (closing.BillCount, closing.Total, closing.VoidedCount, closing.VoidedTotal));
  }

  [Fact]
  public async Task TheSalesReport_ShowsTheRotation_AvailableStock_DailyAverageAndCoverage()
  {
    await ReceiveAsync(_wax.Id, 10, 20000);
    var bill = await OpenWalkInAsync(_barberUser, "Cliente");
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(bill.Version, "Product", ProductId: _wax.Id, Quantity: 4));
    await ApproveAsync(_approverUser, await CloseAsync(_barberUser, bill));

    var inventory = new InventoryManagementService(_dbContext, _clock);
    var sales = await inventory.GetSalesAsync(new InventorySalesQuery(Today.AddDays(-1), Today));

    var row = Assert.Single(sales.Items);
    // 4 units in 2 days: 2 per day; 6 left cover 3 days.
    Assert.Equal((4, 6, 2m, 3m), (row.Units, row.Available, row.DailyUnits!.Value, row.CoverageDays!.Value));
  }
}
