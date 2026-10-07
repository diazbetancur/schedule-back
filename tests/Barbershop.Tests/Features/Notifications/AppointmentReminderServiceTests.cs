using Barbershop.Application.Notifications;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Notifications;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbershop.Tests.Features.Notifications;

public sealed class AppointmentReminderServiceTests : IDisposable
{
  // 14:05 UTC = 09:05 in Bogotá.
  private static readonly DateTimeOffset Now = new(2026, 10, 7, 14, 5, 0, TimeSpan.Zero);
  private static readonly DateTime NowUtc = Now.UtcDateTime;

  private readonly AppDbContext _dbContext;
  private readonly ManualTimeProvider _time = new(Now);
  private readonly AppointmentReminderService _service;
  private readonly StaffProfile _staff;
  private readonly Guid _customerId = Guid.NewGuid();

  public AppointmentReminderServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    var dispatcher = new NotificationDispatcher(_dbContext, _time, new NotificationWorkerSignal(), NullLogger<NotificationDispatcher>.Instance);
    _service = new AppointmentReminderService(_dbContext, dispatcher, _time);

    _staff = new StaffProfile(Guid.NewGuid(), "Carlos", 30, NowUtc.AddMonths(-1));
    _dbContext.StaffProfiles.Add(_staff);
    _dbContext.SaveChanges();
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task DayBeforeReminder_IsSentOnceForConfirmedAppointment()
  {
    // Tomorrow 08:55 Bogotá, booked three days ago.
    await AddAppointmentAsync(NowUtc.AddHours(23).AddMinutes(50), AppointmentStatus.Confirmed, NowUtc.AddDays(-3), _customerId);

    await _service.RunAsync();
    await _service.RunAsync();

    var reminder = Assert.Single(await CustomerRemindersAsync());
    Assert.Equal(_customerId, reminder.UserId);
    Assert.Equal("Recordatorio de tu cita", reminder.Title);
    Assert.Equal("Tienes cita con Carlos mañana a las 08:55.", reminder.Body);
    Assert.Single(_dbContext.NotificationDeliveries.Where(delivery => delivery.UserId == _customerId));
  }

  [Fact]
  public async Task SameDayReminder_IsSentTwoHoursBefore()
  {
    // Today 10:35 Bogotá, booked yesterday.
    await AddAppointmentAsync(NowUtc.AddMinutes(90), AppointmentStatus.Confirmed, NowUtc.AddDays(-1), _customerId);

    await _service.RunAsync();

    var reminder = Assert.Single(await CustomerRemindersAsync());
    Assert.Equal("Tu cita es a las 10:35", reminder.Title);
    Assert.Equal("Te esperamos hoy a las 10:35 con Carlos.", reminder.Body);

    var delivery = await _dbContext.NotificationDeliveries.SingleAsync(item => item.UserId == _customerId);
    Assert.Equal(NowUtc.AddMinutes(90), delivery.ExpiresAt);
  }

  [Fact]
  public async Task PendingAppointment_ReminderSaysItIsNotConfirmedYet()
  {
    await AddAppointmentAsync(NowUtc.AddHours(23), AppointmentStatus.Pending, NowUtc.AddDays(-2), _customerId);

    await _service.RunAsync();

    var reminder = Assert.Single(await CustomerRemindersAsync());
    Assert.Contains("pendiente de confirmación", reminder.Body);
  }

  [Fact]
  public async Task NoReminder_WhenBookedInsideTheWindow_OrCancelled_OrWithoutAccount()
  {
    // Booked one hour ago for 20 h from now: the 24 h reminder would arrive right after booking.
    await AddAppointmentAsync(NowUtc.AddHours(20), AppointmentStatus.Confirmed, NowUtc.AddHours(-1), _customerId);
    await AddAppointmentAsync(NowUtc.AddHours(5), AppointmentStatus.Cancelled, NowUtc.AddDays(-3), _customerId);
    await AddAppointmentAsync(NowUtc.AddHours(6), AppointmentStatus.Confirmed, NowUtc.AddDays(-3), customerUserId: null);

    await _service.RunAsync();

    Assert.Empty(await CustomerRemindersAsync());
  }

  [Fact]
  public async Task RescheduledAppointment_GetsANewReminder()
  {
    var appointment = await AddAppointmentAsync(NowUtc.AddHours(23), AppointmentStatus.Confirmed, NowUtc.AddDays(-3), _customerId);
    await _service.RunAsync();

    var newStart = NowUtc.AddHours(22);
    appointment.UpdateDetails(appointment.CustomerName, null, null, newStart, newStart.AddMinutes(30), null, NowUtc);
    await _dbContext.SaveChangesAsync();
    await _service.RunAsync();

    Assert.Equal(2, (await CustomerRemindersAsync()).Count);
  }

  [Fact]
  public async Task StaffHourlyAgenda_ListsTheFollowingHourOnce()
  {
    // 10:00 and 10:30 Bogotá are in the next hour; 11:15 is not.
    await AddAppointmentAsync(NowUtc.AddMinutes(55), AppointmentStatus.Confirmed, NowUtc.AddDays(-1), _customerId, "Ana Gómez");
    await AddAppointmentAsync(NowUtc.AddMinutes(85), AppointmentStatus.Pending, NowUtc.AddMinutes(-10), customerUserId: null, "Luis Pérez");
    await AddAppointmentAsync(NowUtc.AddMinutes(130), AppointmentStatus.Confirmed, NowUtc.AddDays(-1), customerUserId: null, "Otro Cliente");

    await _service.RunAsync();
    await _service.RunAsync();

    var agenda = Assert.Single(await _dbContext.UserNotifications
        .Where(entry => entry.Type == UserNotificationTypes.StaffHourlyAgenda)
        .ToListAsync());

    Assert.Equal(_staff.UserId, agenda.UserId);
    Assert.Equal("Próxima hora: 2 citas", agenda.Title);
    Assert.Equal("10:00 Ana Gómez · 10:30 Luis Pérez (pendiente)", agenda.Body);
    Assert.Equal("/staff/appointments", agenda.Url);
  }

  [Fact]
  public async Task StaffHourlyAgenda_IsNotSentLateInTheHour()
  {
    _time.Advance(TimeSpan.FromMinutes(25)); // 09:30 Bogotá
    await AddAppointmentAsync(NowUtc.AddMinutes(55), AppointmentStatus.Confirmed, NowUtc.AddDays(-1), _customerId);

    await _service.RunAsync();

    Assert.Empty(await _dbContext.UserNotifications.Where(entry => entry.Type == UserNotificationTypes.StaffHourlyAgenda).ToListAsync());
  }

  private Task<List<UserNotification>> CustomerRemindersAsync()
      => _dbContext.UserNotifications
          .Where(entry => entry.Type == UserNotificationTypes.AppointmentReminder)
          .ToListAsync();

  private async Task<Appointment> AddAppointmentAsync(
      DateTime startsAtUtc,
      AppointmentStatus status,
      DateTime createdAtUtc,
      Guid? customerUserId,
      string customerName = "Cliente Prueba")
  {
    var appointment = new Appointment(
        _staff.Id,
        customerName,
        startsAtUtc,
        startsAtUtc.AddMinutes(30),
        status,
        customerUserId is null ? AppointmentSource.Manual : AppointmentSource.CustomerBooking,
        createdAtUtc,
        customerUserId);

    _dbContext.Appointments.Add(appointment);
    await _dbContext.SaveChangesAsync();
    return appointment;
  }
}
