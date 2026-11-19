using Barbershop.Application.Appointments;
using Barbershop.Application.Bills;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Notifications;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Services;
using Barbershop.Infrastructure.Appointments;
using Barbershop.Infrastructure.Bills;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Features.Bills;

/// <summary>SONDA lote K (ciclo 4) on the bills: customer search, technical limit, consumptions and rescheduling.</summary>
public sealed partial class BillsServiceTests
{
  [Fact]
  public async Task BUG157_TheCustomerSearch_NeedsANameOrAFullEmailOrPhone_AndOnlyReturnsHints()
  {
    _dbContext.Customers.AddRange(
        new CustomerProfile("Carla Gómez", "3001234567", "carla@gmail.com", _admin.Id, DateTime.UtcNow),
        new CustomerProfile("Diego Ruiz", "3109876543", "diego@gmail.com", _admin.Id, DateTime.UtcNow));
    await _dbContext.SaveChangesAsync();

    // Fragments that would walk the base find nothing.
    Assert.Empty(await _bills.SearchCustomersAsync(_admin.Id, "@gmail.com"));
    Assert.Empty(await _bills.SearchCustomersAsync(_admin.Id, "gmail.com"));
    Assert.Empty(await _bills.SearchCustomersAsync(_admin.Id, "carla@gmail"));
    Assert.Empty(await _bills.SearchCustomersAsync(_admin.Id, "300123"));
    Assert.Empty(await _bills.SearchCustomersAsync(_admin.Id, "Ca"));

    var byEmail = Assert.Single(await _bills.SearchCustomersAsync(_admin.Id, " CARLA@gmail.com "));
    Assert.Equal(("Carla Gómez", "•••4567", "c•••@gmail.com"), (byEmail.FullName, byEmail.PhoneHint, byEmail.EmailHint));
    Assert.Equal("Diego Ruiz", Assert.Single(await _bills.SearchCustomersAsync(_admin.Id, "+57 310 987 6543")).FullName);
    Assert.Equal("Carla Gómez", Assert.Single(await _bills.SearchCustomersAsync(_admin.Id, "car")).FullName);
  }

  [Fact]
  public async Task BUG131_ABillThatWouldPassTheTechnicalMaximum_IsA422_NotA500()
  {
    var expensive = new Barbershop.Domain.Products.Product(
        (await _dbContext.ProductCategories.FirstAsync()).Id, "Kit premium", null, 100_000_000, 0, DateTime.UtcNow);
    _dbContext.Products.Add(expensive);
    await _dbContext.SaveChangesAsync();
    var bill = await OpenWalkInAsync(_barberUser, "Cliente");
    for (var i = 0; i < 2; i++)
    {
      bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(bill.Version, "Product", ProductId: expensive.Id, Quantity: 10));
    }

    // 2.000.000.000 so far; 10 more units would be 3.000.000.000.
    var refused = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(bill.Version, "Product", ProductId: expensive.Id, Quantity: 10)));
    Assert.Contains(BillsService.TotalTooLargeMessage, refused.Errors.SelectMany(error => error.Value));
    Assert.Equal(2, (await _bills.GetAsync(_barberUser.Id, bill.Id)).Lines.Count);
  }

  [Fact]
  public async Task BUG145_TwoConsumptionLinesOfTheSameService_SendOneNoticeWithTheRealBalance_AndAQuantityIsRefused()
  {
    var customer = await CreateLinkedCustomerAsync();
    var membership = await SellMembershipAsync(customer, pool: 3);
    var bill = await OpenForCustomerAsync(_barberUser, customer);

    // One unit per line: a quantity was accepted and silently counted as 1.
    await Assert.ThrowsAsync<ValidationProblemException>(() => _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(
        bill.Version, "MembershipConsumption", _barber.Id, ServiceId: _haircut.Id, CustomerMembershipId: membership, Quantity: 2)));
    for (var i = 0; i < 2; i++)
    {
      bill = await _bills.AddLineAsync(_barberUser.Id, bill.Id, new BillLineRequest(
          bill.Version, "MembershipConsumption", _barber.Id, ServiceId: _haircut.Id, CustomerMembershipId: membership));
    }

    await ApproveAsync(_approverUser, await CloseAsync(_barberUser, bill));

    var notices = await _dbContext.UserNotifications.AsNoTracking()
        .Where(notice => notice.Type == UserNotificationTypes.MembershipConsumed).ToListAsync();
    var notice = Assert.Single(notices);
    Assert.Contains("Registramos 2 «", notice.Body);
    Assert.Contains("Te queda 1 de ese servicio.", notice.Body);
    Assert.Single(await _dbContext.NotificationDeliveries.AsNoTracking().Where(delivery => delivery.UserNotificationId == notice.Id).ToListAsync());
    Assert.Equal(2, await _dbContext.MembershipConsumptions.CountAsync());
  }

  [Fact]
  public async Task BUG145_TwoConsumptionLines_NeverTakeMoreThanTheBalance()
  {
    var customer = await CreateLinkedCustomerAsync();
    var membership = await SellMembershipAsync(customer, pool: 2);
    var first = await OpenForCustomerAsync(_barberUser, customer);
    var second = await OpenForCustomerAsync(_barberUser, customer);
    first = await _bills.AddLineAsync(_barberUser.Id, first.Id, new BillLineRequest(
        first.Version, "MembershipConsumption", _barber.Id, ServiceId: _haircut.Id, CustomerMembershipId: membership));
    second = await _bills.AddLineAsync(_barberUser.Id, second.Id, new BillLineRequest(
        second.Version, "MembershipConsumption", _barber.Id, ServiceId: _haircut.Id, CustomerMembershipId: membership));
    await ApproveAsync(_approverUser, await CloseAsync(_barberUser, first));

    var approved = await ApproveAsync(_approverUser, await CloseAsync(_barberUser, second));

    Assert.Equal("Approved", approved.Status);
    var notices = await _dbContext.UserNotifications.AsNoTracking()
        .Where(notice => notice.Type == UserNotificationTypes.MembershipConsumed).OrderBy(notice => notice.CreatedAt).ToListAsync();
    Assert.Contains("Ya no te quedan más de ese servicio.", notices.Last().Body);
  }

  [Fact]
  public async Task BUG155_AnAppointmentWithAnOpenBill_CannotBeRescheduled()
  {
    var customerUserId = _customerUser.Id;
    var startsAt = _clock.GetUtcNow().UtcDateTime.AddMinutes(30);
    var appointment = new Appointment(
        _barber.Id, "Carla Cliente", startsAt, startsAt.AddMinutes(30), AppointmentStatus.Confirmed, AppointmentSource.CustomerBooking,
        DateTime.UtcNow, customerUserId);
    _dbContext.Appointments.Add(appointment);
    await _dbContext.SaveChangesAsync();
    await _bills.OpenAsync(_barberUser.Id, new BillOpenRequest(Guid.NewGuid(), AppointmentId: appointment.Id));
    ICustomerAppointmentsService appointments = new AppointmentManagementService(_dbContext, _clock, new SilentAppointmentNotifications());

    var refused = await Assert.ThrowsAsync<ConflictException>(() => appointments.RescheduleAsync(
        customerUserId, appointment.Id, new CustomerAppointmentRescheduleRequest(startsAt.AddDays(2))));

    Assert.Equal(AppointmentManagementService.OpenBillRescheduleMessage, refused.Message);
    Assert.Equal(startsAt, (await _dbContext.Appointments.AsNoTracking().SingleAsync(candidate => candidate.Id == appointment.Id)).StartsAt);
  }

  private sealed class SilentAppointmentNotifications : IAppointmentNotificationService
  {
    public Task NotifyStaffOfNewAppointmentAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NotifyStaffOfCustomerCancellationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NotifyStaffOfAppointmentRescheduledAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NotifyStaffOfAppointmentCancelledAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NotifyCustomerOfAppointmentUpdateAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NotifyCustomerOfAppointmentCancellationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NotifyCustomerOfAppointmentConfirmationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }
}
