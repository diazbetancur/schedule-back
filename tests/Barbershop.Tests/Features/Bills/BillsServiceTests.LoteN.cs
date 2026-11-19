using Barbershop.Application.Appointments;
using Barbershop.Application.Bills;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Application.Notifications;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Bills;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Memberships;
using Barbershop.Domain.Services;
using Barbershop.Infrastructure.Appointments;
using Barbershop.Infrastructure.Bills;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Features.Bills;

/// <summary>
/// SONDA lote N (cycle 5): lines removed while correcting (ADR-0021 L9), memberships with consumptions in pending bills
/// (L10, L12), the payment method correction (L11), appointments with a bill (L13), rates in pending bills (L14), the
/// customer search (L15), the revalidation of corrections (L17), one line that overflows and the pages of «Por cerrar».
/// The foreign keys that made BUG-177 a 409 are only real in BillsLoteNPostgresTests.
/// </summary>
public sealed partial class BillsServiceTests
{
  // ── BUG-177 ────────────────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task BUG177_ALineRemovedFromAnUnapprovedBill_IsKeptForItsPostings_AndTheNewApprovalPostsOnlyWhatIsLeft()
  {
    await ReceiveAsync(_wax.Id, 5, 10000);
    var bill = await AddServiceAsync(_admin, await OpenWalkInAsync(_admin, "Cliente"));
    bill = await _bills.AddLineAsync(_admin.Id, bill.Id, new BillLineRequest(bill.Version, "Product", ProductId: _wax.Id, Quantity: 1));
    bill = await _bills.AddLineAsync(_admin.Id, bill.Id, new BillLineRequest(bill.Version, "Tip", _barber.Id, Amount: 5000, TipPaymentMode: "WithBill"));
    bill = await ApproveAsync(_approverUser, await CloseAsync(_admin, bill));
    bill = await UnapproveAsync(_approverUser, bill);

    foreach (var type in new[] { "Product", "Tip" })
    {
      var line = bill.Lines.Single(candidate => candidate.Type == type);
      bill = await _bills.RemoveLineAsync(_approverUser.Id, bill.Id, line.Id, bill.Version);
    }

    Assert.Equal((30000, 1), (bill.Total, bill.Lines.Count));
    bill = await ApproveAsync(_approverUser, bill);

    var incomes = await _dbContext.IncomeEntries.AsNoTracking().Where(entry => entry.BillId == bill.Id).ToListAsync();
    Assert.Equal(30000, incomes.Sum(entry => entry.Amount));
    Assert.Equal(0, incomes.Where(entry => entry.Kind != IncomeKind.Service).Sum(entry => entry.Amount));
    var stored = await _dbContext.BillLines.IgnoreQueryFilters().AsNoTracking().Where(line => line.BillId == bill.Id).ToListAsync();
    Assert.Equal(2, stored.Count(line => line.RemovedAt is not null));
  }

  [Fact]
  public async Task BUG177_ALineWithoutPostings_IsStillDeleted()
  {
    var bill = await AddServiceAsync(_admin, await OpenWalkInAsync(_admin, "Cliente"));
    bill = await _bills.AddLineAsync(_admin.Id, bill.Id, new BillLineRequest(bill.Version, "Tip", _barber.Id, Amount: 5000, TipPaymentMode: "Direct"));

    bill = await _bills.RemoveLineAsync(_admin.Id, bill.Id, bill.Lines.Single(line => line.Type == "Tip").Id, bill.Version);

    Assert.Single(bill.Lines);
    Assert.Equal(1, await _dbContext.BillLines.IgnoreQueryFilters().CountAsync(line => line.BillId == bill.Id));
  }

  // ── BUG-170 / BUG-183 / BUG-175 ────────────────────────────────────────────────────────────

  [Fact]
  public async Task BUG170_AMembershipWithAConsumptionInAnOpenBill_IsNotAnnulled_AndTheMessageNamesTheBill()
  {
    var customerId = await CreateLinkedCustomerAsync();
    var membershipId = await SellMembershipAsync(customerId, 3);
    var bill = await OpenForCustomerAsync(_admin, customerId);
    await AddConsumptionAsync(bill, membershipId);

    var refused = await Assert.ThrowsAsync<ConflictException>(() => AnnulAsync(membershipId));
    Assert.Equal(
        $"Esta membresía tiene un consumo en la cuenta abierta de Carla Cliente del {Today:dd/MM/yyyy}. Quita esa línea o anula la cuenta antes de anular la membresía.",
        refused.Message);

    // Closed (waiting for approval): the same, now with its number.
    var closed = await CloseAsync(_admin, bill);
    refused = await Assert.ThrowsAsync<ConflictException>(() => AnnulAsync(membershipId));
    Assert.Contains($"la cuenta {closed.Number} (cerrada, por aprobar)", refused.Message);

    // Voided: nothing holds it any more, and the customer is told (without the internal reason).
    await _bills.VoidAsync(_admin.Id, bill.Id, new BillReasonRequest((await _bills.GetAsync(_admin.Id, bill.Id)).Version, "Se cargó por error"));
    await AnnulAsync(membershipId);
    var notice = await _dbContext.UserNotifications.AsNoTracking()
        .SingleAsync(candidate => candidate.UserId == _customerUser.Id && candidate.Type == UserNotificationTypes.MembershipAnnulled);
    Assert.Equal("Tu membresía fue anulada", notice.Title);
    Assert.DoesNotContain("Prueba de anulación", notice.Body);
    Assert.Equal(NotificationCategories.Memberships, NotificationCategories.ForType(notice.Type));
  }

  [Fact]
  public async Task BUG183_AnUnapprovedBillWithTheConsumption_BlocksTheAnnulment_AndALegacyAnnulledOneCanStillBeFixed()
  {
    var customerId = await CreateLinkedCustomerAsync();
    var membershipId = await SellMembershipAsync(customerId, 3);
    var bill = await OpenForCustomerAsync(_admin, customerId);
    bill = await AddServiceAsync(_admin, await AddConsumptionAsync(bill, membershipId));
    bill = await UnapproveAsync(_approverUser, await ApproveAsync(_approverUser, await CloseAsync(_admin, bill)));

    // The unapproval reversed the consumption (L3 no longer blocks), but the bill being corrected still carries it.
    var refused = await Assert.ThrowsAsync<ConflictException>(() => AnnulAsync(membershipId));
    Assert.Contains($"la cuenta {bill.Number} (en corrección)", refused.Message);

    // A membership annulled before this rule (C-000696 in QA): the bill gets out by removing the line (BUG-177).
    var membership = await _dbContext.CustomerMemberships.SingleAsync(candidate => candidate.Id == membershipId);
    membership.Annul(_admin.Id, "Anulada antes de la regla", DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();
    _dbContext.ChangeTracker.Clear();

    await Assert.ThrowsAsync<ConflictException>(() => ApproveAsync(_approverUser, bill));
    var consumption = bill.Lines.Single(line => line.Type == "MembershipConsumption");
    bill = await _bills.RemoveLineAsync(_approverUser.Id, bill.Id, consumption.Id, bill.Version);
    Assert.Equal(BillStatus.Approved.ToString(), (await ApproveAsync(_approverUser, bill)).Status);
  }

  // ── BUG-172 ────────────────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task BUG172_CorrectingOnlyThePaymentMethod_MovesTheChargedCopy_KeepsTheChargedTotal_AndIsAuditedWithItsReason()
  {
    var card = new PaymentMethod("Tarjeta", 50, DateTime.UtcNow);
    _dbContext.PaymentMethods.Add(card);
    await _dbContext.SaveChangesAsync();
    var bill = await CloseAsync(_admin, await AddServiceAsync(_admin, await OpenWalkInAsync(_admin, "Cliente")));

    var missing = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _bills.ChangePaymentMethodAsync(_approverUser.Id, bill.Id, new BillPaymentMethodRequest(bill.Version, card.Id)));
    Assert.True(missing.Errors.ContainsKey("reason"));

    await _bills.ChangePaymentMethodAsync(_approverUser.Id, bill.Id, new BillPaymentMethodRequest(bill.Version, card.Id, "Pagó con tarjeta"));

    var stored = await _dbContext.Bills.AsNoTracking().SingleAsync(candidate => candidate.Id == bill.Id);
    Assert.Equal((card.Id, "Tarjeta", 30000), (stored.ChargedPaymentMethodId!.Value, stored.ChargedPaymentMethodName, stored.ChargedTotal!.Value));
    var cash = await _bills.GetCashClosingAsync(new CashClosingQuery());
    Assert.Equal(card.Id, Assert.Single(cash.Methods).PaymentMethodId);
    var changed = await _dbContext.BillEvents.AsNoTracking().SingleAsync(candidate => candidate.BillId == bill.Id && candidate.Type == BillEventType.PaymentMethodChanged);
    Assert.Equal((_approverUser.Id, "Pagó con tarjeta"), (changed.UserId, changed.Reason));
    Assert.Contains("Efectivo", changed.BeforeJson);
    Assert.Contains("Tarjeta", changed.AfterJson);
  }

  // ── BUG-157 ────────────────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task BUG157_TheCustomerSearch_IsOnlyForTheAdminAndWhoeverOpensBills()
  {
    await CreateLinkedCustomerAsync();
    Assert.Single(await _bills.SearchCustomersAsync(_admin.Id, "carla"));
    Assert.Single(await _bills.SearchCustomersAsync(_barberUser.Id, "carla")); // Staff holds bills.open
    var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _bills.SearchCustomersAsync(_approverUser.Id, "carla"));
    Assert.Equal(BillsService.CustomerSearchPermissionMessage, refused.Message);
  }

  // ── BUG-182 / BUG-180 ──────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task BUG182_OneProductLineBeyondTheTechnicalMaximum_IsAClear422_WhenAddedOrRaised()
  {
    var gold = new Barbershop.Domain.Products.Product(_wax.CategoryId, "Reloj", null, 2_500_000, 0, DateTime.UtcNow);
    _dbContext.Products.Add(gold);
    await _dbContext.SaveChangesAsync();
    var bill = await OpenWalkInAsync(_admin, "Cliente");

    var added = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _bills.AddLineAsync(_admin.Id, bill.Id, new BillLineRequest(bill.Version, "Product", ProductId: gold.Id, Quantity: 1000)));
    Assert.Contains(BillsService.TotalTooLargeMessage, added.Errors["quantity"]);

    bill = await _bills.AddLineAsync(_admin.Id, bill.Id, new BillLineRequest(bill.Version, "Product", ProductId: gold.Id, Quantity: 500));
    var raised = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _bills.UpdateLineAsync(_admin.Id, bill.Id, bill.Lines.Single().Id, new BillLineRequest(bill.Version, Quantity: 859)));
    Assert.Contains(BillsService.TotalTooLargeMessage, raised.Errors["quantity"]);
  }

  [Fact]
  public async Task BUG180_ALineOfADeletedProductOrRate_RefusesDiscountsCourtesiesAndPrices_AndOnlyGoesDown()
  {
    var bill = await AddServiceAsync(_admin, await OpenWalkInAsync(_admin, "Cliente"));
    bill = await _bills.AddLineAsync(_admin.Id, bill.Id, new BillLineRequest(bill.Version, "Product", ProductId: _wax.Id, Quantity: 2));
    var product = bill.Lines.Single(line => line.Type == "Product");
    var service = bill.Lines.Single(line => line.Type == "Service");

    // The price comes from the catalog: another one is refused (it used to be ignored with a 200).
    var price = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _bills.UpdateLineAsync(_admin.Id, bill.Id, product.Id, new BillLineRequest(bill.Version, UnitPrice: 1)));
    Assert.Contains(BillsService.CatalogPriceMessage, price.Errors["unitPrice"]);
    var units = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _bills.UpdateLineAsync(_admin.Id, bill.Id, service.Id, new BillLineRequest(bill.Version, Quantity: 2)));
    Assert.Contains(BillsService.ServiceUnitMessage, units.Errors["quantity"]);

    (await _dbContext.Products.SingleAsync(candidate => candidate.Id == _wax.Id)).MarkDeleted(DateTime.UtcNow);
    (await _dbContext.Services.SingleAsync(candidate => candidate.Id == _haircut.Id)).MarkDeleted(DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();
    _dbContext.ChangeTracker.Clear();

    var discount = await Assert.ThrowsAsync<ValidationProblemException>(() => _bills.AddLineAsync(_admin.Id, bill.Id,
        new BillLineRequest(bill.Version, "Discount", Amount: 1000, Reason: "Cliente frecuente", AppliesToLineId: product.Id)));
    Assert.Contains(BillsService.ProductGoneMessage, discount.Errors["appliesToLineId"]);
    var serviceDiscount = await Assert.ThrowsAsync<ValidationProblemException>(() => _bills.AddLineAsync(_admin.Id, bill.Id,
        new BillLineRequest(bill.Version, "Discount", Amount: 1000, Reason: "Cliente frecuente", AppliesToLineId: service.Id)));
    Assert.Contains(BillsService.RateGoneMessage, serviceDiscount.Errors["appliesToLineId"]);
    var courtesy = await Assert.ThrowsAsync<ValidationProblemException>(() => _bills.UpdateLineAsync(_admin.Id, bill.Id, service.Id,
        new BillLineRequest(bill.Version, IsCourtesy: true, Reason: "Cortesía de la casa")));
    Assert.Contains(BillsService.RateGoneMessage, courtesy.Errors["isCourtesy"]);

    // Fewer units are still allowed.
    bill = await _bills.UpdateLineAsync(_admin.Id, bill.Id, product.Id, new BillLineRequest(bill.Version, Quantity: 1));
    Assert.Equal(1, bill.Lines.Single(line => line.Type == "Product").Quantity);
  }

  // ── BUG-181 ────────────────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task BUG181_TheOpenBillsOfPorCerrar_ComeByPages()
  {
    for (var index = 0; index < 3; index++)
    {
      await OpenWalkInAsync(_admin, $"Cliente {index}");
    }

    var first = await _bills.GetPendingAsync(_admin.Id, new BillPendingQuery(1, 2));
    var second = await _bills.GetPendingAsync(_admin.Id, new BillPendingQuery(2, 2));
    var beyond = await _bills.GetPendingAsync(_admin.Id, new BillPendingQuery(9, 2));

    Assert.Equal((2, 3, 3), (first.OpenBills.Count, first.OpenBillsTotalCount, first.Count));
    Assert.Equal((1, 2), (second.OpenBills.Count, second.Page));
    Assert.Equal(2, beyond.Page); // out of range → the last page
    Assert.Empty(first.OpenBills.Select(bill => bill.Id).Intersect(second.OpenBills.Select(bill => bill.Id)));
  }

  // ── BUG-178 ────────────────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task BUG178_ARateOnALineOfAPendingBill_IsListedInItsUsage_AndIsNotDeleted()
  {
    var rates = new ServiceManagementService(_dbContext, _clock, new NullCurrentUserAccessor());
    var bill = await AddServiceAsync(_admin, await OpenWalkInAsync(_admin, "Cliente Pendiente"));

    Assert.Equal(["cuenta abierta de Cliente Pendiente"], (await rates.GetUsageAsync(_haircut.Id)).PendingBills);
    var refused = await Assert.ThrowsAsync<ConflictException>(() => rates.SoftDeleteAsync(_haircut.Id));
    Assert.Contains("cuenta abierta de Cliente Pendiente", refused.Message);

    var closed = await CloseAsync(_admin, bill);
    Assert.Equal([closed.Number!], (await rates.GetUsageAsync(_haircut.Id)).PendingBills);

    // Approved: what was sold is respected (L5), the rate can go.
    await ApproveAsync(_approverUser, closed);
    Assert.Empty((await rates.GetUsageAsync(_haircut.Id)).PendingBills!);
    await rates.SoftDeleteAsync(_haircut.Id);
  }

  // ── BUG-179 ────────────────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task BUG179_AnAppointmentWithABill_KeepsItsProfessionalAndItsService()
  {
    IAdminAppointmentsService appointments = new AppointmentManagementService(_dbContext, _clock, new SilentAppointmentNotifications());
    var appointment = await AddAppointmentAsync(AppointmentStatus.Confirmed);
    await _bills.OpenAsync(_admin.Id, new BillOpenRequest(Guid.NewGuid(), AppointmentId: appointment.Id));

    var otherService = await Assert.ThrowsAsync<ConflictException>(() => appointments.UpdateAsync(
        appointment.Id, new AppointmentUpdateRequest(appointment.StartsAt, "Cliente Agenda", null, null, null, GeneralServiceId: Guid.NewGuid())));
    Assert.Equal("Esta cita tiene una cuenta abierta. Anúlala antes de cambiarla.", otherService.Message);
    var otherStaff = await Assert.ThrowsAsync<ConflictException>(() => appointments.UpdateAsync(
        appointment.Id, new AppointmentUpdateRequest(appointment.StartsAt, "Cliente Agenda", null, null, null, StaffProfileId: _otherBarber.Id)));
    Assert.Equal(AppointmentManagementService.BillKeepsAppointmentMessage, otherStaff.Message);

    // Without a bill the professional still never changes: a 422 instead of a silent 200.
    var free = await AddAppointmentAsync(AppointmentStatus.Confirmed);
    var fixedStaff = await Assert.ThrowsAsync<ValidationProblemException>(() => appointments.UpdateAsync(
        free.Id, new AppointmentUpdateRequest(free.StartsAt, "Cliente Agenda", null, null, null, StaffProfileId: _otherBarber.Id)));
    Assert.Contains(AppointmentManagementService.StaffIsFixedMessage, fixedStaff.Errors["staffProfileId"]);
  }

  // ── Helpers ────────────────────────────────────────────────────────────────────────────────

  private Task<BillView> AddConsumptionAsync(BillView bill, Guid membershipId)
      => _bills.AddLineAsync(_admin.Id, bill.Id, new BillLineRequest(
          bill.Version, "MembershipConsumption", _barber.Id, ServiceId: _haircut.Id, CustomerMembershipId: membershipId));

  private Task<CustomerMembershipDetailView> AnnulAsync(Guid membershipId)
      => _memberships.AnnulAsync(_admin.Id, membershipId, new MembershipAnnulRequest("Prueba de anulación"));

  private Task<BillView> UnapproveAsync(Barbershop.Domain.Users.User user, BillView bill)
      => UnapproveAsync(user, bill, "Se registró mal");
}
