using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Finance.Admin;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Application.ServiceRecords;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Common;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Customers;
using Barbershop.Infrastructure.Finance;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.ServiceRecords;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Barbershop.Tests.Features.ServiceRecords;

/// <summary>Who may register and from which appointments, and the non-blocking duplicate warnings of a record.</summary>
public sealed class ServiceRecordsNavigationAndRelationsTests : IDisposable
{
  private const string Terms = "La membresía es personal e intransferible.";

  private readonly DateOnly _today = DateOnly.FromDateTime(BogotaClock.ToLocal(DateTime.UtcNow));
  private readonly AppDbContext _dbContext;
  private readonly ServiceRecordsService _service;
  private readonly MembershipSalesService _memberships;
  private readonly MembershipLedgerService _ledger;
  private readonly IncomeManagementService _income;
  private readonly CustomerManagementService _customers;
  private readonly MembershipPlanManagementService _plans;
  private readonly User _admin;
  private readonly User _barberUser;
  private readonly StaffProfile _barber;
  private readonly StaffProfile _otherBarber;
  private readonly Service _haircut;
  private readonly Service _beard;

  public ServiceRecordsNavigationAndRelationsTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options;
    _dbContext = new AppDbContext(options);
    _dbContext.Database.EnsureCreated();
    _memberships = new MembershipSalesService(_dbContext, TimeProvider.System);
    _ledger = new MembershipLedgerService(_dbContext, _memberships, TimeProvider.System);
    _income = new IncomeManagementService(_dbContext, TimeProvider.System);
    _customers = new CustomerManagementService(_dbContext, TimeProvider.System);
    _plans = new MembershipPlanManagementService(_dbContext, TimeProvider.System, new NullCurrentUserAccessor());
    _service = new ServiceRecordsService(_dbContext, _ledger, TimeProvider.System);

    _admin = new User("Admin Principal", "admin@example.com", "hash", DateTime.UtcNow);
    _barberUser = new User("Alex Barbero", "alex@example.com", "hash", DateTime.UtcNow);
    var otherUser = new User("Beto Barbero", "beto@example.com", "hash", DateTime.UtcNow);
    _haircut = new Service("Corte", 25000, DateTime.UtcNow, 30);
    _beard = new Service("Barba", 15000, DateTime.UtcNow, 30);
    _barber = new StaffProfile(_barberUser.Id, "Alex", 30, DateTime.UtcNow);
    _otherBarber = new StaffProfile(otherUser.Id, "Beto", 30, DateTime.UtcNow);
    _dbContext.Users.AddRange(_admin, _barberUser, otherUser);
    _dbContext.Services.AddRange(_haircut, _beard);
    _dbContext.StaffProfiles.AddRange(_barber, _otherBarber);
    _dbContext.SaveChanges();
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task Eligibility_RequiresAnActiveProfessionalProfile_WhateverTheRole()
  {
    Assert.True((await _service.GetEligibilityAsync(_barberUser.Id)).HasStaffProfile);
    Assert.False((await _service.GetEligibilityAsync(_admin.Id)).HasStaffProfile);

    // An administrator with a professional profile (and no Staff role) can register too.
    var adminProfile = new StaffProfile(_admin.Id, "Admin Barbero", 30, DateTime.UtcNow);
    _dbContext.StaffProfiles.Add(adminProfile);
    await _dbContext.SaveChangesAsync();
    Assert.Equal(adminProfile.Id, (await _service.GetEligibilityAsync(_admin.Id)).StaffProfileId);
    var registered = await _service.RegisterAsync(_admin.Id, new ServiceRecordRegisterRequest(null, _today, null, [_haircut.Id], null, Guid.NewGuid()));
    Assert.Equal(adminProfile.Id, Assert.Single(registered).StaffProfileId);

    // An inactive profile can neither register nor list appointments.
    _barber.UpdateDetails("Alex", null, null, null, null, 30, false, DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();
    Assert.False((await _service.GetEligibilityAsync(_barberUser.Id)).HasStaffProfile);
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
        _service.RegisterAsync(_barberUser.Id, new ServiceRecordRegisterRequest(null, _today, null, [_haircut.Id], null, Guid.NewGuid())));
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.GetAppointmentsAsync(_barberUser.Id));
  }

  [Fact]
  public async Task Appointments_OfferOwnStartedOnes_IncludingCompleted_WithTheServicesAlreadyRegistered()
  {
    var completed = await AddAppointmentAsync(_barber, AppointmentStatus.Completed, hoursAgo: 3);
    var confirmed = await AddAppointmentAsync(_barber, AppointmentStatus.Confirmed, hoursAgo: 1);
    await AddAppointmentAsync(_barber, AppointmentStatus.Cancelled, hoursAgo: 2);
    await AddAppointmentAsync(_barber, AppointmentStatus.NoShow, hoursAgo: 4);
    await AddAppointmentAsync(_barber, AppointmentStatus.Confirmed, hoursAgo: -24);       // future
    await AddAppointmentAsync(_barber, AppointmentStatus.Completed, hoursAgo: 24 * 45);   // too old
    await AddAppointmentAsync(_otherBarber, AppointmentStatus.Completed, hoursAgo: 2);    // someone else's

    // Registering from a completed appointment works and never changes its status.
    await _service.RegisterAsync(_barberUser.Id, new ServiceRecordRegisterRequest(completed.Id, null, null, [_haircut.Id], null, Guid.NewGuid()));

    var options = await _service.GetAppointmentsAsync(_barberUser.Id);

    Assert.Equal([confirmed.Id, completed.Id], options.Select(option => option.Id));
    Assert.Equal([_haircut.Id], options.Single(option => option.Id == completed.Id).RegisteredServiceIds);
    Assert.Equal("Completed", options.Single(option => option.Id == completed.Id).Status);
    Assert.Equal(AppointmentStatus.Completed, (await _dbContext.Appointments.AsNoTracking().SingleAsync(a => a.Id == completed.Id)).Status);

    // A service already registered in the completed appointment cannot be registered again.
    await Assert.ThrowsAsync<ConflictException>(() =>
        _service.RegisterAsync(_barberUser.Id, new ServiceRecordRegisterRequest(completed.Id, null, null, [_haircut.Id], null, Guid.NewGuid())));
  }

  [Fact]
  public async Task Related_WarnsAboutManualIncomeRecordsAndConsumptionsOfTheSameProfessionalServiceAndDay_WithoutBlocking()
  {
    var record = await RegisterAsync(_haircut);
    var twin = await RegisterAsync(_haircut);
    await RegisterAsync(_beard);                                                     // other service: not related

    var manual = await _income.CreateAsync(_admin.Id, new IncomeEntryCreateRequest(_haircut.Id, _barber.Id, 25000, false, _today));
    await _income.CreateAsync(_admin.Id, new IncomeEntryCreateRequest(_haircut.Id, _otherBarber.Id, 25000, false, _today)); // other professional

    var customer = await _customers.CreateAsync(_admin.Id, new CustomerCreateRequest("Carla", "3001234567", "carla@example.com"));
    var plan = await _plans.CreateAsync(new MembershipPlanUpsertRequest("Oro", null, 250000, "Pool", 10, 90, Terms, null, [new(_haircut.Id, null)], []));
    var membership = await _memberships.SellAsync(_admin.Id, customer.Id, new MembershipSaleRequest(plan.Id, _today, ActivateNow: true));
    var consumed = await _ledger.ConsumeAsync(_admin.Id, membership.Id, new MembershipConsumptionRequest(_haircut.Id, 1, _today, _barber.Id, Guid.NewGuid()));

    var related = await _service.GetRelatedAsync(record.Id);

    Assert.Equal([manual.Id], related.ManualIncomes.Select(income => income.Id));
    Assert.Equal([twin.Id], related.OtherRecords.Select(other => other.Id));
    var consumption = Assert.Single(related.MembershipConsumptions);
    Assert.Equal((consumed.Consumptions.Single().Id, false), (consumption.Id, consumption.IsFromServiceRecord));
    Assert.Null(related.AppointmentStatus);

    // Warnings only: the record can still be confirmed, and once confirmed its own income is not reported as manual.
    var approved = await _service.ApproveAsync(_admin.Id, record.Id, new ServiceRecordApproveRequest(
        record.Version, "Paid", _haircut.Id, _barber.Id, null, _today, 25000, null, false, null));
    var afterTwin = await _service.GetRelatedAsync(twin.Id);
    Assert.Equal([manual.Id], afterTwin.ManualIncomes.Select(income => income.Id));
    Assert.DoesNotContain(afterTwin.ManualIncomes, income => income.Id == approved.Approval!.IncomeEntryId);
    Assert.Contains(afterTwin.OtherRecords, other => other.Id == record.Id && other.Status == "Approved");
  }

  [Fact]
  public async Task Related_ReportsTheCurrentStatusOfTheAppointment()
  {
    var appointment = await AddAppointmentAsync(_barber, AppointmentStatus.Confirmed, hoursAgo: 2);
    var record = Assert.Single(await _service.RegisterAsync(_barberUser.Id,
        new ServiceRecordRegisterRequest(appointment.Id, null, null, [_haircut.Id], null, Guid.NewGuid())));

    var tracked = await _dbContext.Appointments.SingleAsync(candidate => candidate.Id == appointment.Id);
    tracked.UpdateStatus(AppointmentStatus.Cancelled, DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();

    Assert.Equal("Cancelled", (await _service.GetRelatedAsync(record.Id)).AppointmentStatus);
  }

  private async Task<ServiceRecordView> RegisterAsync(Service service)
      => Assert.Single(await _service.RegisterAsync(_barberUser.Id,
          new ServiceRecordRegisterRequest(null, _today, null, [service.Id], null, Guid.NewGuid())));

  private async Task<Appointment> AddAppointmentAsync(StaffProfile staff, AppointmentStatus status, int hoursAgo)
  {
    var startsAt = DateTime.UtcNow.AddHours(-hoursAgo);
    var appointment = new Appointment(staff.Id, "Cliente Cita", startsAt, startsAt.AddMinutes(30), status, AppointmentSource.Manual, DateTime.UtcNow);
    _dbContext.Appointments.Add(appointment);
    await _dbContext.SaveChangesAsync();
    return appointment;
  }
}
