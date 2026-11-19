using Barbershop.Application.Bills;
using Barbershop.Domain.Services;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Bills;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Features.Bills;

/// <summary>
/// ADR-0021 L1: a professional puts services and tips only on their own name; another professional's name needs
/// bills.assign-other-staff (Admin by default, not Staff). Lines already on another name are respected.
/// </summary>
public sealed partial class BillsServiceTests
{
  [Fact]
  public async Task L1_ABarberCannotPutAServiceOrATipOnAnotherBarbersName_ButCanOnTheirOwn()
  {
    var bill = await OpenWalkInAsync(_barberUser, "Cliente");

    var service = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(
        bill.Version, "Service", _otherBarber.Id, ServiceCatalogDefaults.CorteGeneralServiceId, _haircut.Id)));
    Assert.Equal(BillsService.AssignOtherStaffMessage, service.Message);
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(
        bill.Version, "Tip", _otherBarber.Id, Amount: 5000, TipPaymentMode: "WithBill")));

    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(
        bill.Version, "Service", _barber.Id, ServiceCatalogDefaults.CorteGeneralServiceId, _haircut.Id));
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(bill.Version, "Tip", _barber.Id, Amount: 5000, TipPaymentMode: "WithBill"));
    // A product's professional is only informative (Q11): not checked.
    bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(bill.Version, "Product", _otherBarber.Id, ProductId: _wax.Id, Quantity: 1));

    Assert.Equal(3, bill.Lines.Count);
    Assert.False((await _bills.GetOptionsAsync(_barberUser.Id)).CanAssignOtherStaff);
  }

  [Fact]
  public async Task L1_ABarberCannotMoveALineToAnotherBarber()
  {
    var bill = await AddServiceAsync(_barberUser, await OpenWalkInAsync(_barberUser, "Cliente"));
    var line = bill.Lines.Single();

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _bills.UpdateLineAsync(
        _barberUser.Id, bill.Id, line.Id, new BillLineRequest(bill.Version, StaffProfileId: _otherBarber.Id, ServiceId: _haircut.Id)));
  }

  [Fact]
  public async Task L1_AdminAndARoleWithThePermission_PutLinesOnAnyProfessional()
  {
    var now = DateTime.UtcNow;
    var counterUser = new User("Mostrador", "mostrador@example.com", "hash", now);
    var counterRole = new Role("Mostrador");
    foreach (var code in new[] { PermissionCodes.BillsOpen, PermissionCodes.BillsManage, PermissionCodes.BillsAssignOtherStaff })
    {
      counterRole.AddPermission((await _dbContext.Permissions.SingleAsync(permission => permission.Code == code)).Id, now);
    }

    _dbContext.Users.Add(counterUser);
    _dbContext.Roles.Add(counterRole);
    _dbContext.UserRoles.Add(new UserRole(counterUser.Id, counterRole.Id, now));
    await _dbContext.SaveChangesAsync();

    var bill = await OpenWalkInAsync(_barberUser, "Cliente");
    bill = await _bills.AddLineAsync(counterUser.Id, bill.Id, new BillLineRequest(
        bill.Version, "Service", _otherBarber.Id, ServiceCatalogDefaults.CorteGeneralServiceId, _haircut.Id));
    bill = await _bills.AddLineAsync(_admin.Id, bill.Id, new BillLineRequest(bill.Version, "Tip", _otherBarber.Id, Amount: 3000, TipPaymentMode: "Direct"));

    Assert.All(bill.Lines, line => Assert.Equal(_otherBarber.Id, line.StaffProfileId));
    Assert.True((await _bills.GetOptionsAsync(counterUser.Id)).CanAssignOtherStaff);
    Assert.True((await _bills.GetOptionsAsync(_admin.Id)).CanAssignOtherStaff);
  }

  [Fact]
  public async Task L1_AnExistingLineOnAnotherBarber_IsRespected_ItsRateCanChange_AndTheBillCloses()
  {
    // An open bill from before the rule: the admin put a service of the other barber on the barber's bill.
    var bill = await OpenWalkInAsync(_barberUser, "Cliente");
    bill = await _bills.AddLineAsync(_admin.Id, bill.Id, new BillLineRequest(
        bill.Version, "Service", _otherBarber.Id, ServiceCatalogDefaults.CorteGeneralServiceId));
    var line = bill.Lines.Single();

    // The barber still completes it: choosing the rate does not change the professional.
    bill = await _bills.UpdateLineAsync(_barberUser.Id, bill.Id, line.Id, new BillLineRequest(
        bill.Version, StaffProfileId: _otherBarber.Id, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId, ServiceId: _haircut.Id));
    bill = await CloseAsync(_barberUser, bill);

    Assert.Equal("Closed", bill.Status);
    Assert.Equal(_otherBarber.Id, bill.Lines.Single().StaffProfileId);
  }
}
