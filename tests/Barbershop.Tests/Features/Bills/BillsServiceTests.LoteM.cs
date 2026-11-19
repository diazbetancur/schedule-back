using Barbershop.Application.Bills;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Bills;
using Barbershop.Domain.Products;
using Barbershop.Domain.Services;
using Barbershop.Infrastructure.Bills;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Features.Bills;

/// <summary>SONDA lote M: a bill is opened only on the day of the service (ADR-0021 L8) and the low findings of the bills.</summary>
public sealed partial class BillsServiceTests
{
  [Fact]
  public async Task L8_AWalkInBill_OpensOnlyWithToday_AndItsDateCannotChange()
  {
    var yesterday = await Assert.ThrowsAsync<ValidationProblemException>(() => _bills.OpenAsync(
        _barberUser.Id, new BillOpenRequest(Guid.NewGuid(), CustomerName: "Cliente", ServiceDate: Today.AddDays(-1))));
    Assert.Contains(BillsService.OnlyTodayMessage, yesterday.Errors["serviceDate"]);

    var bill = await _bills.OpenAsync(_barberUser.Id, new BillOpenRequest(Guid.NewGuid(), CustomerName: "Cliente", ServiceDate: Today));
    Assert.Equal(Today, bill.ServiceDate);
    var moved = await Assert.ThrowsAsync<ValidationProblemException>(() => _bills.UpdateHeaderAsync(
        _barberUser.Id, bill.Id, new BillHeaderRequest(bill.Version, null, null, Today.AddDays(-1), null)));
    Assert.Contains(BillsService.DateIsFixedMessage, moved.Errors["serviceDate"]);
  }

  [Fact]
  public async Task L8_TheBillOfAnAppointmentOfAPastDay_IsNotOpened_NorOffered_AndCompletingItOpensNothing()
  {
    var past = Appointment.ForService(
        _barber.Id, "Cliente Ayer", _clock.GetUtcNow().UtcDateTime.AddDays(-1),
        new AppointmentServiceReservation(ServiceCatalogDefaults.CorteGeneralServiceId, "Corte", 30), AppointmentStatus.Completed,
        AppointmentSource.Manual, DateTime.UtcNow);
    _dbContext.Appointments.Add(past);
    await _dbContext.SaveChangesAsync();

    var refused = await Assert.ThrowsAsync<ConflictException>(() =>
        _bills.OpenAsync(_barberUser.Id, new BillOpenRequest(Guid.NewGuid(), AppointmentId: past.Id)));
    Assert.Equal(BillsService.PastAppointmentMessage, refused.Message);
    Assert.DoesNotContain((await _bills.GetPendingAsync(_barberUser.Id)).AppointmentsWithoutBill, item => item.AppointmentId == past.Id);

    // ADR-0018 D2 still lets it be completed, but no bill is opened for a past day.
    Assert.Null(await BillsService.OpenForCompletedAppointmentAsync(_dbContext, past, _barberUser.Id, DateTime.UtcNow, CancellationToken.None));

    // Today's completed appointment is still offered and opens its bill.
    var today = await AddAppointmentAsync(AppointmentStatus.Completed);
    Assert.Contains((await _bills.GetPendingAsync(_barberUser.Id)).AppointmentsWithoutBill, item => item.AppointmentId == today.Id);
  }

  [Fact]
  public async Task BUG127_ARetryWithTheSameKey_MustCarryTheSameOpeningData()
  {
    var key = Guid.NewGuid();
    var bill = await _bills.OpenAsync(_barberUser.Id, new BillOpenRequest(key, CustomerName: "Cliente Uno", Notes: "Llega tarde"));

    Assert.Equal(bill.Id, (await _bills.OpenAsync(_barberUser.Id, new BillOpenRequest(key, CustomerName: "cliente uno", Notes: "Llega tarde"))).Id);
    await Assert.ThrowsAsync<ConflictException>(() => _bills.OpenAsync(_barberUser.Id, new BillOpenRequest(key, CustomerName: "Otro Cliente")));
    await Assert.ThrowsAsync<ConflictException>(() => _bills.OpenAsync(_barberUser.Id, new BillOpenRequest(key, CustomerName: "Cliente Uno", Notes: "Otra nota")));
    await Assert.ThrowsAsync<ConflictException>(() => _bills.OpenAsync(
        _barberUser.Id, new BillOpenRequest(key, CustomerName: "Cliente Uno", StaffProfileId: _otherBarber.Id)));
  }

  [Fact]
  public async Task BUG128_TheApprovalPreview_IsOnlyForBillsThatCanBeApproved()
  {
    var open = await AddServiceAsync(_barberUser, await OpenWalkInAsync(_barberUser, "Cliente"));
    await Assert.ThrowsAsync<ConflictException>(() => _bills.PreviewAsync(_approverUser.Id, open.Id));

    var closed = await CloseAsync(_barberUser, open);
    Assert.NotEmpty((await _bills.PreviewAsync(_approverUser.Id, closed.Id)).Lines);

    var approved = await ApproveAsync(_admin, closed);
    await Assert.ThrowsAsync<ConflictException>(() => _bills.PreviewAsync(_approverUser.Id, approved.Id));

    var toVoid = await AddServiceAsync(_barberUser, await OpenWalkInAsync(_barberUser, "Otro"));
    var voided = await _bills.VoidAsync(_admin.Id, toVoid.Id, new BillReasonRequest(toVoid.Version, "Error de registro"));
    await Assert.ThrowsAsync<ConflictException>(() => _bills.PreviewAsync(_approverUser.Id, voided.Id));
  }

  [Fact]
  public async Task BUG132_TheStockCardSaysWhichBillTheUnitsLeftWith()
  {
    await ReceiveAsync(_wax.Id, 5, 10000);
    var bill = await OpenWalkInAsync(_barberUser, "Cliente");
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(bill.Version, "Product", ProductId: _wax.Id, Quantity: 2));
    bill = await ApproveAsync(_admin, await CloseAsync(_barberUser, bill));

    var sale = await _dbContext.InventoryMovements.AsNoTracking().SingleAsync(movement => movement.Type == InventoryMovementType.Sale);
    Assert.Equal($"Cuenta {bill.Number}", sale.Reference);
  }

  [Fact]
  public async Task BUG156_ACorrectionCannotAddUnitsOfAProductThatIsNoLongerSold_ButCanRemoveThem()
  {
    var bill = await OpenWalkInAsync(_barberUser, "Cliente");
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(bill.Version, "Product", ProductId: _wax.Id, Quantity: 2));
    var line = bill.Lines.Single();
    var product = await _dbContext.Products.SingleAsync(candidate => candidate.Id == _wax.Id);
    product.Deactivate(DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();

    await Assert.ThrowsAsync<ValidationProblemException>(() => _bills.UpdateLineAsync(
        _barberUser.Id, bill.Id, line.Id, new BillLineRequest(bill.Version, Quantity: 3)));
    bill = await _bills.UpdateLineAsync(_barberUser.Id, bill.Id, line.Id, new BillLineRequest(bill.Version, Quantity: 1));

    Assert.Equal(1, bill.Lines.Single().Quantity);
  }
}
