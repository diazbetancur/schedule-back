using Barbershop.Application.Notifications;
using Barbershop.Application.Reviews;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Notifications;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Reviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Barbershop.Tests.Features.Reviews;

/// <summary>
/// ADR-0021 L4: a review tells the professional of the appointment and the administrators (stars and comment), in the
/// category «Reseñas», which only professionals and administrators see in their preferences.
/// </summary>
public sealed class ReviewNotificationsTests : IDisposable
{
  // 10:15 in Bogotá on 2026-01-05.
  private static readonly DateTimeOffset Now = new(2026, 1, 5, 15, 15, 0, TimeSpan.Zero);

  private readonly AppDbContext _dbContext;
  private readonly ICustomerReviewsService _reviews;
  private readonly User _admin;
  private readonly User _inactiveAdmin;
  private readonly User _barberUser;
  private readonly User _customer;
  private readonly StaffProfile _barber;

  public ReviewNotificationsTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options;
    _dbContext = new AppDbContext(options);
    var clock = new FixedClock(Now);
    _reviews = new ReviewManagementService(_dbContext, clock);

    var now = Now.UtcDateTime;
    _admin = new User("Admin Principal", "admin@example.com", "hash", now);
    _inactiveAdmin = new User("Admin Retirado", "retirado@example.com", "hash", now);
    _inactiveAdmin.Deactivate(now);
    _barberUser = new User("Alex Barbero", "alex@example.com", "hash", now);
    _customer = new User("Carla Cliente", "carla@example.com", "hash", now);
    _dbContext.Users.AddRange(_admin, _inactiveAdmin, _barberUser, _customer);

    var adminRole = new Role(RoleNames.Admin, isSystemRole: true);
    var staffRole = new Role(RoleNames.Staff, isSystemRole: true);
    var customerRole = new Role(RoleNames.Customer, isSystemRole: true);
    _dbContext.Roles.AddRange(adminRole, staffRole, customerRole);
    _dbContext.UserRoles.AddRange(
        new UserRole(_admin.Id, adminRole.Id, now),
        new UserRole(_inactiveAdmin.Id, adminRole.Id, now),
        new UserRole(_barberUser.Id, staffRole.Id, now),
        new UserRole(_customer.Id, customerRole.Id, now));

    _barber = new StaffProfile(_barberUser.Id, "Alex", 30, now);
    _dbContext.StaffProfiles.Add(_barber);
    _dbContext.SaveChanges();
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task L4_AReview_NotifiesTheProfessionalAndTheActiveAdmins_WithStarsAndComment()
  {
    var appointment = await AddCompletedAppointmentAsync();

    await _reviews.CreateAsync(_customer.Id, appointment.Id, new ReviewCreateRequest(4, "Muy buen corte"));

    var notices = await _dbContext.UserNotifications.AsNoTracking().ToListAsync();
    Assert.Equal(
        new[] { _admin.Id, _barberUser.Id }.Order(),
        notices.Select(notice => notice.UserId).Order());
    Assert.All(notices, notice =>
    {
      Assert.Equal(UserNotificationTypes.ReviewReceived, notice.Type);
      Assert.Equal("Nueva reseña: 4 de 5 estrellas", notice.Title);
      Assert.Contains("«Muy buen corte»", notice.Body);
      Assert.Equal($"/staff/{_barber.Id}", notice.Url);
    });
    Assert.Contains("calificó tu cita del 4 de enero", notices.Single(notice => notice.UserId == _barberUser.Id).Body);
    Assert.Contains("con Alex", notices.Single(notice => notice.UserId == _admin.Id).Body);
    // One push per notice, sent by the worker with each user's preferences.
    Assert.Equal(2, await _dbContext.NotificationDeliveries.CountAsync());
    Assert.Equal(NotificationCategories.Reviews, NotificationCategories.ForType(UserNotificationTypes.ReviewReceived));
  }

  [Fact]
  public async Task L4_AReviewWithoutComment_SaysSo_AndAnAdminWhoIsTheProfessional_GetsItOnce()
  {
    var now = Now.UtcDateTime;
    var adminRole = await _dbContext.Roles.SingleAsync(role => role.Name == RoleNames.Admin);
    _dbContext.UserRoles.Add(new UserRole(_barberUser.Id, adminRole.Id, now));
    await _dbContext.SaveChangesAsync();
    var appointment = await AddCompletedAppointmentAsync();

    await _reviews.CreateAsync(_customer.Id, appointment.Id, new ReviewCreateRequest(1, null));

    var notices = await _dbContext.UserNotifications.AsNoTracking().ToListAsync();
    Assert.Equal(2, notices.Count);
    Assert.Single(notices, notice => notice.UserId == _barberUser.Id);
    Assert.All(notices, notice => Assert.Equal("Nueva reseña: 1 de 5 estrellas", notice.Title));
    Assert.All(notices, notice => Assert.EndsWith("y no dejó comentario.", notice.Body));
  }

  [Fact]
  public async Task L4_ARejectedReview_NotifiesNobody()
  {
    var appointment = await AddCompletedAppointmentAsync();
    await _reviews.CreateAsync(_customer.Id, appointment.Id, new ReviewCreateRequest(5, "Excelente"));
    _dbContext.ChangeTracker.Clear();

    await Assert.ThrowsAnyAsync<Exception>(() => _reviews.CreateAsync(_customer.Id, appointment.Id, new ReviewCreateRequest(5, "Otra vez")));

    Assert.Equal(2, await _dbContext.UserNotifications.CountAsync());
  }

  [Fact]
  public async Task L4_TheReviewsPreference_IsOfferedOnlyToStaffAndAdmins()
  {
    var service = new UserNotificationsService(_dbContext, new FixedClock(Now));

    var customer = await service.GetPreferencesAsync(_customer.Id);
    var barber = await service.GetPreferencesAsync(_barberUser.Id);
    var admin = await service.GetPreferencesAsync(_admin.Id);

    Assert.DoesNotContain(customer.Categories, preference => preference.Category == NotificationCategories.Reviews);
    Assert.Contains(barber.Categories, preference => preference.Category == NotificationCategories.Reviews && preference.PushEnabled);
    Assert.Contains(admin.Categories, preference => preference.Category == NotificationCategories.Reviews);

    var updated = await service.UpdatePreferencesAsync(_barberUser.Id, new NotificationPreferencesUpdateRequest(
        [new NotificationPreferenceUpdate(NotificationCategories.Reviews, false)]));
    Assert.False(updated.Categories.Single(preference => preference.Category == NotificationCategories.Reviews).PushEnabled);
  }

  private async Task<Appointment> AddCompletedAppointmentAsync()
  {
    var startsAt = Now.UtcDateTime.AddDays(-1);
    var appointment = new Appointment(
        _barber.Id, "Carla Cliente", startsAt, startsAt.AddMinutes(30), AppointmentStatus.Completed, AppointmentSource.CustomerBooking,
        startsAt.AddDays(-2), _customer.Id);
    _dbContext.Appointments.Add(appointment);
    await _dbContext.SaveChangesAsync();
    return appointment;
  }

  private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
  {
    public override DateTimeOffset GetUtcNow() => utcNow;
  }
}
