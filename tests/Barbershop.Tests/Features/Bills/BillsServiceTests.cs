using Barbershop.Application.Bills;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Finance.Admin;
using Barbershop.Application.Loyalty.Redemptions;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Bills;
using Barbershop.Domain.Common;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.Products;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Bills;
using Barbershop.Infrastructure.Finance;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Loyalty;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Barbershop.Tests.Features.Bills;

/// <summary>
/// ADR-0020 stages 2 and 3: the rules of the bill and of its approval with postings. Locks, real concurrency and the
/// database constraints are in BillsPostgresTests; permissions over HTTP in BillsHttpIntegrationTests.
/// </summary>
public sealed partial class BillsServiceTests : IDisposable
{
  private const string Terms = "La membresía es personal e intransferible.";

  private readonly MutableClock _clock = new(DateTimeOffset.UtcNow);
  private readonly AppDbContext _dbContext;
  private readonly BillsService _bills;
  private readonly MembershipSalesService _memberships;
  private readonly MembershipPlanManagementService _plans;
  private readonly LoyaltyRedemptionService _redemptions;
  private readonly IncomeManagementService _income;
  private readonly User _admin;
  private readonly User _barberUser;
  private readonly User _otherBarberUser;
  private readonly User _approverUser;
  private readonly User _customerUser;
  private readonly StaffProfile _barber;
  private readonly StaffProfile _otherBarber;
  private readonly Service _haircut;
  private readonly Product _wax;
  private readonly Guid _cash = new("5d0c3a0e-6a4b-4f5e-9b0a-1c2d3e4f5a01");

  public BillsServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options;
    _dbContext = new AppDbContext(options);
    _dbContext.Database.EnsureCreated(); // seeds the payment methods, the number series and the loyalty settings
    _memberships = new MembershipSalesService(_dbContext, _clock);
    var ledger = new MembershipLedgerService(_dbContext, _memberships, _clock);
    _plans = new MembershipPlanManagementService(_dbContext, _clock, new NullCurrentUserAccessor());
    _redemptions = new LoyaltyRedemptionService(_dbContext, _clock);
    _income = new IncomeManagementService(_dbContext, _clock);
    _bills = new BillsService(_dbContext, ledger, _clock);

    var now = DateTime.UtcNow;
    _admin = new User("Admin Principal", "admin@example.com", "hash", now);
    _barberUser = new User("Alex Barbero", "alex@example.com", "hash", now);
    _otherBarberUser = new User("Beto Barbero", "beto@example.com", "hash", now);
    _approverUser = new User("Ana Aprobadora", "ana@example.com", "hash", now);
    _customerUser = new User("Carla Cliente", "carla@example.com", "hash", now);
    _dbContext.Users.AddRange(_admin, _barberUser, _otherBarberUser, _approverUser, _customerUser);

    var permissions = PermissionCodes.All.ToDictionary(code => code, code => new Permission(code, code, now));
    _dbContext.Permissions.AddRange(permissions.Values);
    var adminRole = new Role(RoleNames.Admin, isSystemRole: true);
    foreach (var permission in permissions.Values)
    {
      adminRole.RolePermissions.Add(new RolePermission(adminRole.Id, permission.Id, now));
    }

    // Q16: Staff opens, closes and charges; no discounts (Q2).
    var staffRole = new Role(RoleNames.Staff, isSystemRole: true);
    foreach (var code in new[] { PermissionCodes.BillsOpen, PermissionCodes.BillsClose, PermissionCodes.BillsCharge })
    {
      staffRole.RolePermissions.Add(new RolePermission(staffRole.Id, permissions[code].Id, now));
    }

    // An approver without the Admin role (ADR-0018 D4: permissions are enough).
    var approverRole = new Role("Aprobación");
    approverRole.AddPermission(permissions[PermissionCodes.BillsApprove].Id, now);
    approverRole.AddPermission(permissions[PermissionCodes.BillsVoid].Id, now);
    approverRole.AddPermission(permissions[PermissionCodes.BillsUnapprove].Id, now);
    _dbContext.Roles.AddRange(adminRole, staffRole, approverRole);
    _dbContext.UserRoles.AddRange(
        new UserRole(_admin.Id, adminRole.Id, now),
        new UserRole(_barberUser.Id, staffRole.Id, now),
        new UserRole(_otherBarberUser.Id, staffRole.Id, now),
        new UserRole(_approverUser.Id, approverRole.Id, now));

    _barber = new StaffProfile(_barberUser.Id, "Alex", 30, now);
    _otherBarber = new StaffProfile(_otherBarberUser.Id, "Beto", 30, now);
    _dbContext.StaffProfiles.AddRange(_barber, _otherBarber);
    _haircut = new Service("Corte", 30000, now, businessPercentage: 40);
    _haircut.SetLoyaltyPoints(10, now);
    _dbContext.Services.Add(_haircut);
    var category = new ProductCategory("Ceras", 1, now);
    _dbContext.ProductCategories.Add(category);
    _wax = new Product(category.Id, "Cera mate", null, 35000, 0, now);
    _dbContext.Products.Add(_wax);
    _dbContext.SaveChanges();
  }

  public void Dispose() => _dbContext.Dispose();

  private DateOnly Today => DateOnly.FromDateTime(BogotaClock.ToLocal(_clock.GetUtcNow().UtcDateTime));

  // ── Opening and lines ──────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task ABarber_OpensAWalkInBill_AddsLines_AndTheTotalsFollowTheLines()
  {
    var bill = await OpenWalkInAsync(_barberUser, "Cliente de paso");
    Assert.Equal(("Open", null, "Cliente de paso", true, _barber.Id), (bill.Status, bill.Number, bill.CustomerName, bill.IsWalkIn, bill.StaffProfileId));

    bill = await AddServiceAsync(_barberUser, bill);
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(bill.Version, "Product", ProductId: _wax.Id, Quantity: 2));
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(bill.Version, "Tip", _barber.Id, Amount: 5000, TipPaymentMode: "WithBill"));
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(bill.Version, "Tip", _barber.Id, Amount: 2000, TipPaymentMode: "Direct"));

    Assert.Equal((100000, 0, 5000, 2000, 105000), (bill.Subtotal, bill.DiscountTotal, bill.TipTotal, bill.DirectTipTotal, bill.Total));
    var service = bill.Lines[0];
    Assert.Equal(("Corte · Corte", 30000, 40), (service.Name, service.UnitPrice, service.BusinessPercentage!.Value));
    // P10: not enough stock is a warning, never a block.
    Assert.Contains(bill.Warnings, warning => warning.Contains("Stock insuficiente de «Cera mate»"));
    Assert.Equal(5, await _dbContext.BillEvents.CountAsync(billEvent => billEvent.BillId == bill.Id));
  }

  [Fact]
  public async Task Q4_AWalkInBill_CannotUseMembershipsRedemptionsOrPoints()
  {
    var bill = await OpenWalkInAsync(_barberUser, "Cliente de paso");

    await Assert.ThrowsAsync<ConflictException>(() => _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(
        bill.Version, "MembershipConsumption", _barber.Id, ServiceId: _haircut.Id, CustomerMembershipId: Guid.NewGuid())));
    await Assert.ThrowsAsync<ConflictException>(() => _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(
        bill.Version, "Redemption", _barber.Id, LoyaltyRedemptionId: Guid.NewGuid())));
  }

  [Fact]
  public async Task Q2AndQ10_OnlyAdminOrBillsDiscount_GivesDiscountsAndCourtesies_AlwaysWithAReason_NeverAboveWhatTheyDiscount()
  {
    var bill = await AddServiceAsync(_barberUser, await OpenWalkInAsync(_barberUser, "Cliente"));
    var serviceLine = bill.Lines[0].Id;

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _bills.AddLineAsync(_barberUser.Id, bill.Id,
        new BillLineRequest(bill.Version, "Discount", Amount: 1000, Reason: "Cliente frecuente")));
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _bills.UpdateLineAsync(_barberUser.Id, bill.Id, serviceLine,
        new BillLineRequest(bill.Version, IsCourtesy: true, Reason: "Cumpleaños")));

    var missingReason = await Assert.ThrowsAsync<ValidationProblemException>(() => _bills.AddLineAsync(_admin.Id, bill.Id,
        new BillLineRequest(bill.Version, "Discount", Amount: 1000, AppliesToLineId: serviceLine)));
    Assert.Contains("reason", missingReason.Errors.Keys);
    var tooMuch = await Assert.ThrowsAsync<ConflictException>(() => _bills.AddLineAsync(_admin.Id, bill.Id,
        new BillLineRequest(bill.Version, "Discount", Amount: 30001, Reason: "Promoción", AppliesToLineId: serviceLine)));
    Assert.Equal(BillsService.DiscountTooLargeMessage, tooMuch.Message);

    // No cap besides the value itself: the admin may discount the whole line.
    bill = await _bills.AddLineAsync(_admin.Id, bill.Id, new BillLineRequest(bill.Version, "Discount", Amount: 30000, Reason: "Promoción", AppliesToLineId: serviceLine));
    Assert.Equal((30000, 30000, 0), (bill.Subtotal, bill.DiscountTotal, bill.Total));
    // A discounted line cannot become a courtesy, nor be removed, until its discount goes.
    await Assert.ThrowsAsync<ConflictException>(() => _bills.RemoveLineAsync(_admin.Id, bill.Id, serviceLine, bill.Version));
  }

  [Fact]
  public async Task OtherBarbersCannotSeeOrEditTheBill()
  {
    var bill = await OpenWalkInAsync(_barberUser, "Cliente");
    var current = await AddServiceAsync(_barberUser, bill);

    await Assert.ThrowsAsync<NotFoundException>(() => _bills.GetAsync(_otherBarberUser.Id, bill.Id));
    await Assert.ThrowsAsync<NotFoundException>(() => AddServiceAsync(_otherBarberUser, current));
    Assert.Empty((await _bills.ListAsync(_otherBarberUser.Id, new BillListQuery())).Items);
    Assert.Single((await _bills.ListAsync(_admin.Id, new BillListQuery())).Items);
  }

  // ── Closing ────────────────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task CloseAndCharge_AssignsTheGaplessNumber_ThePaymentMethod_AndNotifiesTheCustomer()
  {
    var customer = await CreateLinkedCustomerAsync();
    var first = await AddServiceAsync(_barberUser, await OpenForCustomerAsync(_barberUser, customer));

    // A failed close never spends a number.
    var withoutMethod = await Assert.ThrowsAsync<ValidationProblemException>(() => _bills.CloseAsync(_barberUser.Id, first.Id, new BillCloseRequest(first.Version)));
    Assert.Contains("paymentMethodId", withoutMethod.Errors.Keys);
    var closed = await _bills.CloseAsync(_barberUser.Id, first.Id, new BillCloseRequest(first.Version, _cash));
    Assert.Equal(("Closed", "C-000001", "Efectivo", 30000), (closed.Status, closed.Number, closed.PaymentMethodName, closed.Total));

    var second = await CloseAsync(_barberUser, await AddServiceAsync(_barberUser, await OpenWalkInAsync(_barberUser, "Otro")));
    Assert.Equal("C-000002", second.Number);

    // A retried close returns the stored one; it does not take another number.
    Assert.Equal("C-000001", (await _bills.CloseAsync(_barberUser.Id, first.Id, new BillCloseRequest(first.Version, _cash))).Number);
    Assert.Equal(3, (await _dbContext.BillNumberSeries.SingleAsync()).NextNumber);

    var notice = await _dbContext.UserNotifications.SingleAsync(notification => notification.UserId == _customerUser.Id);
    Assert.Equal(("bill.closed", "Tu recibo de caja C-000001"), (notice.Type, notice.Title));
    Assert.Contains("$ 30.000", notice.Body);
    Assert.Contains("Efectivo", notice.Body);
  }

  [Fact]
  public async Task Closing_RequiresARateForEveryService_AndChargingNeedsBillsCharge()
  {
    var bill = await OpenWalkInAsync(_barberUser, "Cliente");
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(
        bill.Version, "Service", _barber.Id, ServiceCatalogDefaults.CorteGeneralServiceId));

    var withoutRate = await Assert.ThrowsAsync<ConflictException>(() => _bills.CloseAsync(_barberUser.Id, bill.Id, new BillCloseRequest(bill.Version, _cash)));
    Assert.Contains("Elige la tarifa", withoutRate.Message);

    await RevokeFromStaffAsync(PermissionCodes.BillsCharge);
    bill = await _bills.UpdateLineAsync(_barberUser.Id, bill.Id, bill.Lines[0].Id, new BillLineRequest(
        bill.Version, ServiceId: _haircut.Id, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _bills.CloseAsync(_barberUser.Id, bill.Id, new BillCloseRequest(bill.Version, _cash)));
  }

  [Fact]
  public async Task Q17_ABarberCannotCorrectAClosedBill_TheApproverEditsItWithAudit_OrItIsVoided()
  {
    var bill = await CloseAsync(_barberUser, await AddServiceAsync(_barberUser, await OpenWalkInAsync(_barberUser, "Cliente")));

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
        _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(bill.Version, "Product", ProductId: _wax.Id, Quantity: 1)));

    var edited = await _bills.AddLineAsync(_approverUser.Id, bill.Id, new BillLineRequest(bill.Version, "Product", ProductId: _wax.Id, Quantity: 1));
    Assert.Equal(("Closed", 65000, "C-000001"), (edited.Status, edited.Total, edited.Number));
    var audit = await _dbContext.BillEvents.SingleAsync(billEvent => billEvent.BillId == bill.Id && billEvent.StatusAtEvent == BillStatus.Closed);
    Assert.Equal((BillEventType.LineAdded, _approverUser.Id), (audit.Type, audit.UserId));

    var voided = await _bills.VoidAsync(_approverUser.Id, edited.Id, new BillReasonRequest(edited.Version, "Se cobró dos veces"));
    Assert.Equal(("Voided", "C-000001", "Se cobró dos veces"), (voided.Status, voided.Number, voided.VoidReason));
    await Assert.ThrowsAsync<ConflictException>(() =>
        _bills.AddLineAsync(_admin.Id, bill.Id, new BillLineRequest(voided.Version, "Product", ProductId: _wax.Id, Quantity: 1)));
  }

  [Fact]
  public async Task Q15_ClosingTheBillOfAnAppointment_CompletesIt_AndACancelledAppointmentMustBeDetachedFirst()
  {
    var confirmed = await AddAppointmentAsync(AppointmentStatus.Confirmed);
    var bill = await _bills.OpenAsync(_barberUser.Id, new BillOpenRequest(Guid.NewGuid(), confirmed.Id));
    Assert.Equal("Corte", Assert.Single(bill.Lines).Name);
    await Assert.ThrowsAsync<ConflictException>(() => _bills.OpenAsync(_barberUser.Id, new BillOpenRequest(Guid.NewGuid(), confirmed.Id)));

    bill = await _bills.UpdateLineAsync(_barberUser.Id, bill.Id, bill.Lines[0].Id, new BillLineRequest(
        bill.Version, ServiceId: _haircut.Id, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));
    await CloseAsync(_barberUser, bill);
    Assert.Equal(AppointmentStatus.Completed, (await _dbContext.Appointments.AsNoTracking().SingleAsync(a => a.Id == confirmed.Id)).Status);

    var cancelledLater = await AddAppointmentAsync(AppointmentStatus.Confirmed);
    var other = await _bills.OpenAsync(_barberUser.Id, new BillOpenRequest(Guid.NewGuid(), cancelledLater.Id));
    other = await _bills.UpdateLineAsync(_barberUser.Id, other.Id, other.Lines[0].Id, new BillLineRequest(
        other.Version, ServiceId: _haircut.Id, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));
    var stored = await _dbContext.Appointments.SingleAsync(a => a.Id == cancelledLater.Id);
    stored.UpdateStatus(AppointmentStatus.Cancelled, DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();

    var refused = await Assert.ThrowsAsync<ConflictException>(() => _bills.CloseAsync(_barberUser.Id, other.Id, new BillCloseRequest(other.Version, _cash)));
    Assert.Equal(BillsService.CancelledAppointmentMessage, refused.Message);
    other = await _bills.GetAsync(_barberUser.Id, other.Id);
    Assert.True(other.Actions.CanDetachAppointment);
    other = await _bills.DetachAppointmentAsync(_barberUser.Id, other.Id, new BillReasonRequest(other.Version, "El servicio sí se prestó"));
    Assert.Null(other.AppointmentId);
    Assert.Equal("Closed", (await CloseAsync(_barberUser, other)).Status);
  }

  [Fact]
  public async Task Q5_AnOpenBillNeverExpires_ButAfterSevenDaysItShowsAsOverdue()
  {
    var bill = await OpenWalkInAsync(_barberUser, "Cliente");
    Assert.False((await _bills.GetPendingAsync(_barberUser.Id)).OpenBills.Single().IsOverdue);

    _clock.Advance(TimeSpan.FromDays(Bill.OverdueAfterDays));

    var pending = await _bills.GetPendingAsync(_barberUser.Id);
    Assert.True(Assert.Single(pending.OpenBills).IsOverdue);
    Assert.Equal("Open", (await _bills.GetAsync(_barberUser.Id, bill.Id)).Status);
    Assert.Equal(1, (await _bills.GetPendingCountAsync(_barberUser.Id)).Count);
  }

  // ── Approval and postings (stage 3) ────────────────────────────────────────────────────────

  [Fact]
  public async Task Approval_PostsEveryLine_P7CommissionOnTheFullPrice_Q11ProductsForTheBusiness_Q3Tips()
  {
    await EnableLoyaltyAsync();
    await ReceiveAsync(_wax.Id, 1, 20000);
    var customer = await CreateLinkedCustomerAsync();
    var bill = await AddServiceAsync(_barberUser, await OpenForCustomerAsync(_barberUser, customer));
    var serviceLine = bill.Lines[0].Id;
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(bill.Version, "Product", ProductId: _wax.Id, Quantity: 2));
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(bill.Version, "Tip", _barber.Id, Amount: 5000, TipPaymentMode: "WithBill"));
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(bill.Version, "Tip", _barber.Id, Amount: 3000, TipPaymentMode: "Direct"));
    bill = await _bills.AddLineAsync(_admin.Id, bill.Id, new BillLineRequest(bill.Version, "Discount", Amount: 6000, Reason: "Promoción", AppliesToLineId: serviceLine));
    bill = await _bills.AddLineAsync(_admin.Id, bill.Id, new BillLineRequest(bill.Version, "Service", _otherBarber.Id, ServiceCatalogDefaults.CorteGeneralServiceId, _haircut.Id, IsCourtesy: true, Reason: "Cumpleaños"));
    bill = await CloseAsync(_barberUser, bill);
    Assert.Equal((100000, 6000, 5000, 99000), (bill.Subtotal, bill.DiscountTotal, bill.TipTotal, bill.Total));

    var approved = await ApproveAsync(_approverUser, bill);

    Assert.Equal(("Approved", false), (approved.Status, approved.IsSelfApproved));
    var incomes = await _dbContext.IncomeEntries.AsNoTracking().ToListAsync();
    var service = Assert.Single(incomes, income => income.Kind == IncomeKind.Service);
    // P7: charged 24.000 after the discount, commission computed on the full 30.000 (60 % = 18.000): the business bears it.
    Assert.Equal((24000, 30000, 18000, 6000), (service.Amount, service.CommissionBaseAmount!.Value, service.ProfessionalAmount, service.BusinessAmount));
    var product = Assert.Single(incomes, income => income.Kind == IncomeKind.ProductSale);
    Assert.Equal((70000, 0, 70000, 2, 20000m), (product.Amount, product.ProfessionalAmount, product.BusinessAmount, product.Quantity!.Value, product.UnitCostSnapshot!.Value));
    var tip = Assert.Single(incomes, income => income.Kind == IncomeKind.Tip);
    Assert.Equal((5000, 5000, _barber.Id), (tip.Amount, tip.ProfessionalAmount, tip.StaffProfileId!.Value));
    // The courtesy and the direct tip create nothing; the incomes add up to what was charged.
    Assert.Equal(3, incomes.Count);
    Assert.Equal(bill.Total, incomes.Sum(income => income.Amount));
    Assert.All(incomes, income => Assert.Equal((bill.Id, bill.ServiceDate), (income.BillId!.Value, income.OccurredOn)));

    // P10: the stock goes negative at the average cost; the card says so.
    var stock = await _dbContext.ProductStocks.AsNoTracking().SingleAsync(row => row.ProductId == _wax.Id);
    Assert.Equal(-1, stock.OnHand);
    var sale = await _dbContext.InventoryMovements.AsNoTracking().SingleAsync(movement => movement.Type == InventoryMovementType.Sale);
    Assert.Equal((2, -1, 20000m), (sale.Quantity, sale.OnHandAfter, sale.UnitCost!.Value));

    // Points: only the paid service, from the rate's points; never the courtesy (ADR-0009).
    var lot = await _dbContext.LoyaltyLots.AsNoTracking().SingleAsync();
    Assert.Equal((LoyaltySourceType.BillLine, 10, customer), (lot.SourceType, lot.InitialPoints, lot.CustomerId));
    Assert.Equal(5, await _dbContext.BillPostings.CountAsync());

    // The incomes belong to the bill: Income neither edits nor deletes them, and shows its number.
    var view = (await _income.GetAsync(new IncomeListQuery(Today, Today))).Items.Single(item => item.Kind == "Service");
    Assert.Equal(("C-000001", 30000), (view.BillNumber, view.CommissionBase!.Value));
    await Assert.ThrowsAsync<ConflictException>(() => _income.SoftDeleteAsync(view.Id));
  }

  [Fact]
  public async Task Approval_IsIdempotent_AndASecondDecisionIsAConflict()
  {
    var bill = await CloseAsync(_barberUser, await AddServiceAsync(_barberUser, await OpenWalkInAsync(_barberUser, "Cliente")));
    var request = new BillApproveRequest(bill.Version, Guid.NewGuid());

    await _bills.ApproveAsync(_approverUser.Id, bill.Id, request);
    var replay = await _bills.ApproveAsync(_approverUser.Id, bill.Id, request);

    Assert.Equal("Approved", replay.Status);
    Assert.Single(await _dbContext.IncomeEntries.ToListAsync());
    await Assert.ThrowsAsync<ConflictException>(() => _bills.ApproveAsync(_admin.Id, bill.Id, new BillApproveRequest(replay.Version, Guid.NewGuid())));
    await Assert.ThrowsAsync<ConflictException>(() => _bills.VoidAsync(_admin.Id, bill.Id, new BillReasonRequest(replay.Version, "Error")));
  }

  [Fact]
  public async Task P4_NobodyApprovesTheirOwnBill_ButAnAdminMay_AndItIsMarkedSelfApproved()
  {
    // A barber who is also an approver (not Admin) is refused for a bill with a line of theirs.
    var approverStaff = new StaffProfile(_approverUser.Id, "Ana", 30, DateTime.UtcNow);
    _dbContext.StaffProfiles.Add(approverStaff);
    await _dbContext.SaveChangesAsync();
    var bill = await OpenWalkInAsync(_barberUser, "Cliente");
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(
        bill.Version, "Service", approverStaff.Id, ServiceCatalogDefaults.CorteGeneralServiceId, _haircut.Id));
    bill = await CloseAsync(_barberUser, bill);
    var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ApproveAsync(_approverUser, bill));
    Assert.Equal(BillsService.SelfApprovalForbiddenMessage, refused.Message);
    Assert.Empty(await _dbContext.IncomeEntries.ToListAsync());

    // The Admin as customer may approve, and the bill and its income say so.
    var adminProfile = new CustomerProfile("Admin Cliente", "3009998877", "admin-cliente@example.com", _admin.Id, DateTime.UtcNow);
    adminProfile.LinkAccount(_admin.Id, _admin.Id, DateTime.UtcNow);
    _dbContext.Customers.Add(adminProfile);
    await _dbContext.SaveChangesAsync();
    var own = await CloseAsync(_barberUser, await AddServiceAsync(_barberUser, await OpenForCustomerAsync(_barberUser, adminProfile.Id)));
    var approved = await ApproveAsync(_admin, own);
    Assert.True(approved.IsSelfApproved);
    Assert.True((await _income.GetAsync(new IncomeListQuery(Today, Today))).Items.Single().IsSelfApproved);
  }

  [Fact]
  public async Task MembershipConsumption_IsUsedOnApproval_AndAMembershipAnnulledMeanwhile_StopsTheWholeApproval()
  {
    var customer = await CreateLinkedCustomerAsync();
    var membership = await SellMembershipAsync(customer, pool: 1);
    var bill = await OpenForCustomerAsync(_barberUser, customer);
    var options = await _bills.GetMembershipOptionsAsync(_barberUser.Id, bill.Id);
    Assert.Equal(1, Assert.Single(Assert.Single(options).Services).Available);
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(
        bill.Version, "MembershipConsumption", _barber.Id, ServiceId: _haircut.Id, CustomerMembershipId: membership));

    // The balance is counted with the lines already on the bill.
    await Assert.ThrowsAsync<ConflictException>(() => _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(
        bill.Version, "MembershipConsumption", _barber.Id, ServiceId: _haircut.Id, CustomerMembershipId: membership)));
    bill = await CloseAsync(_barberUser, bill);
    Assert.Equal((0, (string?)null), (bill.Total, bill.PaymentMethodName));

    var sold = await _dbContext.CustomerMemberships.SingleAsync(candidate => candidate.Id == membership);
    sold.Annul(_admin.Id, "Pago devuelto", DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();
    _dbContext.ChangeTracker.Clear();

    await Assert.ThrowsAnyAsync<ConflictException>(() => ApproveAsync(_approverUser, bill));
    Assert.Empty(await _dbContext.MembershipConsumptions.ToListAsync());
    Assert.Empty(await _dbContext.BillApprovals.ToListAsync());
  }

  [Fact]
  public async Task AMembershipConsumption_IsPostedThroughTheLedger_WithoutIncomeOrCommission()
  {
    var customer = await CreateLinkedCustomerAsync();
    var membership = await SellMembershipAsync(customer, pool: 2);
    var bill = await OpenForCustomerAsync(_barberUser, customer);
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(
        bill.Version, "MembershipConsumption", _barber.Id, ServiceId: _haircut.Id, CustomerMembershipId: membership));

    await ApproveAsync(_approverUser, await CloseAsync(_barberUser, bill));

    var consumption = await _dbContext.MembershipConsumptions.AsNoTracking().SingleAsync();
    Assert.Equal((membership, _haircut.Id, _barber.Id), (consumption.CustomerMembershipId, consumption.ServiceId, consumption.StaffProfileId!.Value));
    Assert.Empty(await _dbContext.IncomeEntries.Where(income => income.Kind != IncomeKind.MembershipSale).ToListAsync());
  }

  [Fact]
  public async Task C8_ARedemptionNeedsTheDeliverPermission_IsDeliveredByItsLine_AndCannotSitOnTwoBills()
  {
    var customer = await CreateLinkedCustomerAsync();
    var redemptionId = await ApprovedServiceRedemptionAsync(customer);
    var bill = await OpenForCustomerAsync(_barberUser, customer);
    Assert.Single(await _bills.GetRedemptionOptionsAsync(_barberUser.Id, bill.Id));
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(bill.Version, "Redemption", _barber.Id, LoyaltyRedemptionId: redemptionId));
    var other = await OpenForCustomerAsync(_barberUser, customer);
    Assert.Empty(await _bills.GetRedemptionOptionsAsync(_barberUser.Id, other.Id));
    await Assert.ThrowsAsync<ConflictException>(() => _bills.AddLineAsync(_barberUser.Id, other.Id,
        new BillLineRequest(other.Version, "Redemption", _barber.Id, LoyaltyRedemptionId: redemptionId)));
    bill = await CloseAsync(_barberUser, bill);

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ApproveAsync(_approverUser, bill));

    await ApproveAsync(_admin, bill);
    var redemption = await _dbContext.LoyaltyRedemptions.AsNoTracking().SingleAsync(candidate => candidate.Id == redemptionId);
    Assert.Equal((LoyaltyRedemptionStatus.Delivered, bill.Lines[0].Id, (Guid?)null), (redemption.Status, redemption.BillLineId!.Value, redemption.ServiceRecordId));
    Assert.Empty(await _dbContext.IncomeEntries.Where(income => income.BillId != null).ToListAsync());
  }

  [Fact]
  public async Task AMergedCustomer_IsPostedToTheLiveProfile()
  {
    await EnableLoyaltyAsync();
    var origin = await CreateLinkedCustomerAsync();
    var bill = await CloseAsync(_barberUser, await AddServiceAsync(_barberUser, await OpenForCustomerAsync(_barberUser, origin)));
    var live = new CustomerProfile("Carla Unificada", "3005556677", "carla2@example.com", _admin.Id, DateTime.UtcNow);
    _dbContext.Customers.Add(live);
    (await _dbContext.Customers.SingleAsync(customer => customer.Id == origin)).MarkMergedInto(live.Id, _admin.Id, DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();
    Assert.Contains((await _bills.GetAsync(_approverUser.Id, bill.Id)).Warnings, warning => warning.Contains("se fusionó"));

    await ApproveAsync(_approverUser, bill);

    Assert.Equal(live.Id, (await _dbContext.LoyaltyLots.AsNoTracking().SingleAsync()).CustomerId);
    Assert.Equal(live.Id, (await _dbContext.BillApprovals.AsNoTracking().SingleAsync()).CustomerId);
  }

  [Fact]
  public async Task APriceChangeAfterTheClose_DoesNotChangeWhatIsApproved_AndTheApproverIsWarned()
  {
    var bill = await CloseAsync(_barberUser, await AddServiceAsync(_barberUser, await OpenWalkInAsync(_barberUser, "Cliente")));
    var rate = await _dbContext.Services.SingleAsync(candidate => candidate.Id == _haircut.Id);
    rate.Update("Corte", 45000, DateTime.UtcNow, 10);
    await _dbContext.SaveChangesAsync();

    var preview = await _bills.PreviewAsync(_approverUser.Id, bill.Id);
    var line = Assert.Single(preview.Lines);
    Assert.Equal((30000, 45000, true, 18000), (line.Net, line.CatalogPriceNow!.Value, line.PriceChanged, line.ProfessionalAmount));
    Assert.Contains(preview.Warnings, warning => warning.Contains("precio de catálogo cambió"));

    await ApproveAsync(_approverUser, bill);
    var income = await _dbContext.IncomeEntries.AsNoTracking().SingleAsync();
    Assert.Equal((30000, 30000, 40), (income.Amount, income.BasePriceSnapshot, income.BusinessPercentageSnapshot));
  }

  [Fact]
  public async Task ADeactivatedBarber_KeepsTheirClosedBillsApprovable_AndTheirCommission()
  {
    var bill = await CloseAsync(_barberUser, await AddServiceAsync(_barberUser, await OpenWalkInAsync(_barberUser, "Cliente")));
    _dbContext.Entry(await _dbContext.StaffProfiles.SingleAsync(staff => staff.Id == _barber.Id)).Property(nameof(StaffProfile.IsActive)).CurrentValue = false;
    await _dbContext.SaveChangesAsync();

    Assert.False((await _bills.ListAsync(_approverUser.Id, new BillListQuery("Closed"))).Items.Single().StaffIsActive);
    Assert.False(Assert.Single((await _bills.PreviewAsync(_approverUser.Id, bill.Id)).Lines).StaffIsActive);
    await ApproveAsync(_approverUser, bill);

    Assert.Equal(_barber.Id, (await _dbContext.IncomeEntries.AsNoTracking().SingleAsync()).StaffProfileId);
    // A deactivated professional can no longer open bills.
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => OpenWalkInAsync(_barberUser, "Otro"));
  }

  [Theory]
  [InlineData(new[] { 30000, 35000 }, 6500, new[] { 3000, 3500 })]
  [InlineData(new[] { 10000, 10000, 10000 }, 100, new[] { 34, 33, 33 })]
  [InlineData(new[] { 1, 1 }, 1, new[] { 1, 0 })]
  public void ADiscountOnTheWholeBill_IsSpreadInProportion_ByLargestRemainder(int[] prices, int discount, int[] expected)
  {
    var bill = new Bill(Guid.NewGuid(), null, null, "Cliente", null, Today, null, _admin.Id, DateTime.UtcNow);
    var lines = prices.Select(price => bill.AddLine(BillLine.Product(_wax.Id, "Cera", 1, price, null, DateTime.UtcNow), DateTime.UtcNow)).ToList();
    bill.AddLine(BillLine.Discount(discount, "Promoción", null, DateTime.UtcNow), DateTime.UtcNow);

    var shares = BillsService.AllocateDiscounts(bill);

    Assert.Equal(expected, lines.Select(line => shares[line.Id].Discount));
    Assert.Equal(prices.Sum() - discount, shares.Values.Sum(share => share.Net));
  }

  // ── Helpers ────────────────────────────────────────────────────────────────────────────────

  private Task<BillView> OpenWalkInAsync(User user, string name)
      => _bills.OpenAsync(user.Id, new BillOpenRequest(Guid.NewGuid(), CustomerName: name));

  private Task<BillView> OpenForCustomerAsync(User user, Guid customerId)
      => _bills.OpenAsync(user.Id, new BillOpenRequest(Guid.NewGuid(), CustomerId: customerId));

  private Task<BillView> AddServiceAsync(User user, BillView bill)
      => _bills.AddLineAsync(user.Id, bill.Id, new BillLineRequest(
          bill.Version, "Service", _barber.Id, ServiceCatalogDefaults.CorteGeneralServiceId, _haircut.Id));

  private async Task<BillView> CloseAsync(User user, BillView bill)
  {
    var current = await _bills.GetAsync(user.Id, bill.Id);
    return await _bills.CloseAsync(user.Id, bill.Id, new BillCloseRequest(current.Version, current.Total > 0 ? _cash : null));
  }

  private async Task<BillView> ApproveAsync(User user, BillView bill)
  {
    var current = await _bills.GetAsync(user.Id, bill.Id);
    return await _bills.ApproveAsync(user.Id, bill.Id, new BillApproveRequest(current.Version, Guid.NewGuid()));
  }

  private async Task<Guid> CreateLinkedCustomerAsync()
  {
    var profile = new CustomerProfile("Carla Cliente", "3001234567", "carla@example.com", _admin.Id, DateTime.UtcNow);
    profile.LinkAccount(_customerUser.Id, _admin.Id, DateTime.UtcNow);
    _dbContext.Customers.Add(profile);
    await _dbContext.SaveChangesAsync();
    return profile.Id;
  }

  private async Task<Guid> SellMembershipAsync(Guid customerId, int pool)
  {
    var plan = await _plans.CreateAsync(new MembershipPlanUpsertRequest("Oro", null, 250000, "Pool", pool, 90, Terms, null, [new(_haircut.Id, null)], []));
    return (await _memberships.SellAsync(_admin.Id, customerId, new MembershipSaleRequest(plan.Id, Today, ActivateNow: true))).Id;
  }

  private async Task<Guid> ApprovedServiceRedemptionAsync(Guid customerId)
  {
    await EnableLoyaltyAsync();
    var now = _clock.GetUtcNow().UtcDateTime;
    var lot = LoyaltyLot.Accrue(customerId, LoyaltySourceType.Adjustment, Guid.NewGuid(), 100, now, Today, 365);
    _dbContext.LoyaltyLots.Add(lot);
    _dbContext.LoyaltyMovements.Add(new LoyaltyMovement(customerId, LoyaltyMovementType.AdjustmentCredit, 100, now, lotId: lot.Id, performedByUserId: _admin.Id));
    await _dbContext.SaveChangesAsync();
    var reward = await _redemptions.CreateAsync(_admin.Id, new LoyaltyRewardUpsertRequest("Corte gratis", null, "Service", _haircut.Id, 50));
    var redemption = await _redemptions.CreateAtCounterAsync(
        _approverUser.Id, customerId, new LoyaltyCounterRedemptionRequest(reward.Id, InPersonVerified: true, ApproveNow: true, IdempotencyKey: Guid.NewGuid()));
    _dbContext.ChangeTracker.Clear();
    return redemption.Id;
  }

  private async Task EnableLoyaltyAsync()
  {
    var settings = await _dbContext.LoyaltySettings.SingleAsync();
    if (!settings.IsEnabled)
    {
      settings.Update(true, 365, _admin.Id, DateTime.UtcNow);
      await _dbContext.SaveChangesAsync();
    }
  }

  private async Task ReceiveAsync(Guid productId, int quantity, int unitCost)
  {
    var stock = new ProductStock(productId, DateTime.UtcNow);
    _dbContext.ProductStocks.Add(stock);
    _dbContext.InventoryMovements.Add(stock.Receive(quantity, unitCost, Today, null, null, null, Guid.NewGuid(), _admin.Id, DateTime.UtcNow));
    await _dbContext.SaveChangesAsync();
  }

  private async Task<Appointment> AddAppointmentAsync(AppointmentStatus status)
  {
    var appointment = Appointment.ForService(
        _barber.Id, "Cliente Agenda", _clock.GetUtcNow().UtcDateTime.AddHours(-1),
        new AppointmentServiceReservation(ServiceCatalogDefaults.CorteGeneralServiceId, "Corte", 30), status, AppointmentSource.Manual, DateTime.UtcNow);
    _dbContext.Appointments.Add(appointment);
    await _dbContext.SaveChangesAsync();
    return appointment;
  }

  private async Task RevokeFromStaffAsync(string code)
  {
    var grant = await _dbContext.RolePermissions.SingleAsync(rolePermission => rolePermission.Role.Name == RoleNames.Staff && rolePermission.Permission.Code == code);
    _dbContext.RolePermissions.Remove(grant);
    await _dbContext.SaveChangesAsync();
  }

  private sealed class MutableClock(DateTimeOffset start) : TimeProvider
  {
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
  }
}
