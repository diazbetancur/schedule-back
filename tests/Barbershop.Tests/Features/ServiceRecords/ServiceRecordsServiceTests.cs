using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Finance.Admin;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Application.ServiceRecords;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Common;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.ServiceRecords;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Customers;
using Barbershop.Infrastructure.Finance;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.ServiceRecords;
using Barbershop.Tests.Features.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Barbershop.Tests.Features.ServiceRecords;

/// <summary>Rules of registering and reviewing services. Locks and real concurrency are in ServiceRecordsHttpIntegrationTests.</summary>
public sealed class ServiceRecordsServiceTests : IDisposable
{
  private const string Terms = "La membresía es personal e intransferible.";

  private readonly DateOnly _today = DateOnly.FromDateTime(BogotaClock.ToLocal(DateTime.UtcNow));
  private readonly AppDbContext _dbContext;
  private readonly ServiceRecordsService _service;
  private readonly MembershipSalesService _memberships;
  private readonly MembershipLedgerService _ledger;
  private readonly MembershipPlanManagementService _plans;
  private readonly CustomerManagementService _customers;
  private readonly IncomeManagementService _income;
  private readonly User _admin;
  private readonly User _barberUser;
  private readonly User _customerUser;
  private readonly StaffProfile _barber;
  private readonly StaffProfile _otherBarber;
  private readonly Service _haircut;
  private readonly Service _beard;

  // ADR-0016 B4: the professional registers general services; the rates are chosen when confirming.
  private readonly GeneralService _beardGeneral;

  public ServiceRecordsServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        // The in-memory provider has no transactions; the reviews open one for PostgreSQL.
        .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options;

    _dbContext = new AppDbContext(options);
    _dbContext.Database.EnsureCreated(); // seeds the loyalty settings row
    _memberships = new MembershipSalesService(_dbContext, TimeProvider.System);
    _ledger = new MembershipLedgerService(_dbContext, _memberships, TimeProvider.System);
    _plans = new MembershipPlanManagementService(_dbContext, TimeProvider.System, new NullCurrentUserAccessor());
    _customers = new CustomerManagementService(_dbContext, TimeProvider.System);
    _income = new IncomeManagementService(_dbContext, TimeProvider.System);
    _service = new ServiceRecordsService(_dbContext, _ledger, TimeProvider.System);

    _admin = new User("Admin Principal", "admin@example.com", "hash", DateTime.UtcNow);
    _barberUser = new User("Alex Barbero", "alex@example.com", "hash", DateTime.UtcNow);
    var otherBarberUser = new User("Beto Barbero", "beto@example.com", "hash", DateTime.UtcNow);
    _customerUser = new User("Carla Cliente", "carla@example.com", "hash", DateTime.UtcNow);
    _beardGeneral = new GeneralService(ServiceCatalogDefaults.BarberiaCategoryId, "Barba", null, 1, DateTime.UtcNow);
    _dbContext.GeneralServices.Add(_beardGeneral);
    _haircut = new Service("Corte", 25000, DateTime.UtcNow, 30);
    _haircut.SetLoyaltyPoints(10, DateTime.UtcNow);
    _beard = new Service("Barba", 15000, DateTime.UtcNow, 40, _beardGeneral.Id);
    _barber = new StaffProfile(_barberUser.Id, "Alex", 30, DateTime.UtcNow);
    _otherBarber = new StaffProfile(otherBarberUser.Id, "Beto", 30, DateTime.UtcNow);
    _dbContext.Users.AddRange(_admin, _barberUser, otherBarberUser, _customerUser);
    _dbContext.Services.AddRange(_haircut, _beard);
    _dbContext.StaffProfiles.AddRange(_barber, _otherBarber);
    _dbContext.SaveChanges();
  }

  public void Dispose() => _dbContext.Dispose();

  // ── Registration ───────────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task Register_WithoutAppointment_StoresPendingRecords_ForTheProfessionalsOwnProfile()
  {
    var records = await RegisterAsync(serviceIds: [_haircut.Id, _beard.Id], customerName: "Cliente de paso");

    Assert.Equal(2, records.Count);
    Assert.All(records, record =>
    {
      Assert.Equal("Pending", record.Status);
      Assert.Equal(_barber.Id, record.StaffProfileId);
      Assert.Equal("Alex Barbero", record.RegisteredByName);
      Assert.Equal("Cliente de paso", record.CustomerName);
      Assert.Null(record.Approval);
    });
    Assert.Equal(["Corte", "Barba"], records.Select(record => record.ServiceName));
    Assert.Empty(await _dbContext.IncomeEntries.ToListAsync());
  }

  [Fact]
  public async Task Register_FromAnAppointment_TakesItsDateAndCustomer_AndAllowsSeveralServices_WithoutChangingIt()
  {
    var customer = await CreateCustomerAsync(linkedUser: _customerUser);
    var appointment = await CreateAppointmentAsync(_barber, AppointmentStatus.Confirmed, customerUser: _customerUser);

    var records = await RegisterAsync(appointmentId: appointment.Id, performedOn: null, serviceIds: [_haircut.Id, _beard.Id]);

    Assert.All(records, record =>
    {
      Assert.Equal(appointment.Id, record.AppointmentId);
      Assert.Equal(DateOnly.FromDateTime(BogotaClock.ToLocal(appointment.StartsAt)), record.PerformedOn);
      Assert.Equal(customer.Id, record.CustomerId);
    });
    var stored = await _dbContext.Appointments.AsNoTracking().SingleAsync(candidate => candidate.Id == appointment.Id);
    Assert.Equal(AppointmentStatus.Confirmed, stored.Status);
  }

  [Fact]
  public async Task Register_OnAnotherProfessionalsAppointment_IsNotFound()
  {
    var appointment = await CreateAppointmentAsync(_otherBarber, AppointmentStatus.Completed);

    await Assert.ThrowsAsync<NotFoundException>(() =>
        RegisterAsync(appointmentId: appointment.Id, performedOn: null, serviceIds: [_haircut.Id]));
    Assert.Empty(await _dbContext.ServiceRecords.ToListAsync());
  }

  [Theory]
  [InlineData(AppointmentStatus.Cancelled)]
  [InlineData(AppointmentStatus.NoShow)]
  public async Task Register_OnACancelledOrMissedAppointment_IsAConflict(AppointmentStatus status)
  {
    var appointment = await CreateAppointmentAsync(_barber, status);

    await Assert.ThrowsAsync<ConflictException>(() =>
        RegisterAsync(appointmentId: appointment.Id, performedOn: null, serviceIds: [_haircut.Id]));
  }

  /// <summary>
  /// SONDA BUG-042 / ADR-0009: only appointments that already started can carry services. Same rule as the list of
  /// appointments offered to register (StartsAt &lt;= now): an appointment of today that starts later is rejected.
  /// </summary>
  [Fact]
  public async Task Register_OnAnAppointmentOfTodayThatHasNotStarted_IsRejected_AndOneThatStartedIsAccepted()
  {
    // 10:00 in Bogotá: the 15:00 appointment of the same day has not started, the 09:00 one has.
    var day = new DateOnly(2030, 3, 14);
    var clock = new ManualTimeProvider(new DateTimeOffset(BogotaClock.ToUtc(day, new TimeOnly(10, 0)), TimeSpan.Zero));
    var service = new ServiceRecordsService(_dbContext, _ledger, clock);
    var later = await CreateAppointmentAtAsync(BogotaClock.ToUtc(day, new TimeOnly(15, 0)));
    var started = await CreateAppointmentAtAsync(BogotaClock.ToUtc(day, new TimeOnly(9, 0)));

    var notYet = await Assert.ThrowsAsync<ValidationProblemException>(() => service.RegisterAsync(
        _barberUser.Id, new ServiceRecordRegisterRequest(later.Id, null, null, [GeneralOf(_haircut.Id)], null, Guid.NewGuid())));
    Assert.Contains("appointmentId", notYet.Errors.Keys);
    Assert.Empty(await _dbContext.ServiceRecords.Where(record => record.AppointmentId == later.Id).ToListAsync());

    // One minute before it starts it is still rejected; once it starts it is accepted.
    clock.Now = new DateTimeOffset(later.StartsAt.AddMinutes(-1), TimeSpan.Zero);
    await Assert.ThrowsAsync<ValidationProblemException>(() => service.RegisterAsync(
        _barberUser.Id, new ServiceRecordRegisterRequest(later.Id, null, null, [GeneralOf(_haircut.Id)], null, Guid.NewGuid())));

    clock.Now = new DateTimeOffset(later.StartsAt, TimeSpan.Zero);
    var accepted = await service.RegisterAsync(
        _barberUser.Id, new ServiceRecordRegisterRequest(later.Id, null, null, [GeneralOf(_haircut.Id)], null, Guid.NewGuid()));
    Assert.Equal(day, Assert.Single(accepted).PerformedOn);

    var earlier = await service.RegisterAsync(
        _barberUser.Id, new ServiceRecordRegisterRequest(started.Id, null, null, [GeneralOf(_haircut.Id)], null, Guid.NewGuid()));
    Assert.Single(earlier);
  }

  [Fact]
  public async Task Register_TheSameServiceTwiceOnOneAppointment_IsAConflict_UnlessTheFirstWasRejected()
  {
    var appointment = await CreateAppointmentAsync(_barber, AppointmentStatus.Completed);
    var first = (await RegisterAsync(appointmentId: appointment.Id, performedOn: null, serviceIds: [_haircut.Id])).Single();

    await Assert.ThrowsAsync<ConflictException>(() =>
        RegisterAsync(appointmentId: appointment.Id, performedOn: null, serviceIds: [_haircut.Id]));

    await _service.RejectAsync(_admin.Id, first.Id, new ServiceRecordRejectRequest(first.Version, "No se realizó"));
    var again = await RegisterAsync(appointmentId: appointment.Id, performedOn: null, serviceIds: [_haircut.Id]);
    Assert.Single(again);
  }

  [Fact]
  public async Task Register_RetriedWithTheSameKey_ReturnsTheStoredRecords_AndADifferentPayloadIsAConflict()
  {
    var key = Guid.NewGuid();
    var first = await RegisterAsync(serviceIds: [_haircut.Id], key: key);
    var retried = await RegisterAsync(serviceIds: [_haircut.Id], key: key);

    Assert.Equal(first.Select(record => record.Id), retried.Select(record => record.Id));
    Assert.Equal(1, await _dbContext.ServiceRecords.CountAsync());
    await Assert.ThrowsAsync<ConflictException>(() => RegisterAsync(serviceIds: [_beard.Id], key: key));
  }

  [Fact]
  public async Task Register_ValidatesDatesServicesAndProfile()
  {
    var future = await Assert.ThrowsAsync<ValidationProblemException>(() => RegisterAsync(performedOn: _today.AddDays(1)));
    Assert.Contains("performedOn", future.Errors.Keys);

    var noDate = await Assert.ThrowsAsync<ValidationProblemException>(() => _service.RegisterAsync(
        _barberUser.Id, new ServiceRecordRegisterRequest(null, null, null, [_haircut.Id], null, Guid.NewGuid())));
    Assert.Contains("performedOn", noDate.Errors.Keys);

    var repeated = await Assert.ThrowsAsync<ValidationProblemException>(() => RegisterAsync(serviceIds: [_haircut.Id, _haircut.Id]));
    Assert.Contains("generalServiceIds", repeated.Errors.Keys);

    // A rate is not a general service: the professional registers what they did, not how it is settled.
    var rate = await Assert.ThrowsAsync<ValidationProblemException>(() => _service.RegisterAsync(
        _barberUser.Id, new ServiceRecordRegisterRequest(null, _today, null, [_haircut.Id], null, Guid.NewGuid())));
    Assert.Contains("generalServiceIds[0]", rate.Errors.Keys);

    _beardGeneral.Deactivate(DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();
    var inactive = await Assert.ThrowsAsync<ValidationProblemException>(() => RegisterAsync(serviceIds: [_beard.Id]));
    Assert.Contains("generalServiceIds[0]", inactive.Errors.Keys);

    // Without a professional profile nobody can register, whatever their permissions.
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
        _service.RegisterAsync(_admin.Id, new ServiceRecordRegisterRequest(null, _today, null, [_haircut.Id], null, Guid.NewGuid())));
  }

  // ── Paid service ───────────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task ApprovePaid_CreatesExactlyOneServiceIncome_WithTheCurrentSnapshots_AndARetryDoesNotDuplicateIt()
  {
    var record = (await RegisterAsync()).Single();
    var request = Approve(record, "Paid", chargedAmount: 22000);

    var approved = await _service.ApproveAsync(_admin.Id, record.Id, request);
    var retried = await _service.ApproveAsync(_admin.Id, record.Id, request);

    var income = Assert.Single(await _dbContext.IncomeEntries.ToListAsync());
    Assert.Equal(IncomeKind.Service, income.Kind);
    Assert.Equal((_haircut.Id, "Corte", 25000, 30), (income.ServiceId!.Value, income.ServiceNameSnapshot!, income.BasePriceSnapshot, income.BusinessPercentageSnapshot));
    Assert.Equal((22000, 6600, 15400), (income.Amount, income.BusinessAmount, income.ProfessionalAmount));
    Assert.Equal(_barber.Id, income.StaffProfileId);
    Assert.Equal(record.PerformedOn, income.OccurredOn);
    Assert.Equal(_admin.Id, income.CreatedByUserId);
    Assert.Equal(income.Id, approved.Approval!.IncomeEntryId);
    Assert.Equal(approved.Approval.IncomeEntryId, retried.Approval!.IncomeEntryId);
    Assert.Equal("Admin Principal", approved.Approval.ApprovedByName);

    // A second decision on a confirmed record is refused.
    await Assert.ThrowsAsync<ConflictException>(() =>
        _service.ApproveAsync(_admin.Id, record.Id, Approve(record, "Paid", chargedAmount: 30000)));
    Assert.Single(await _dbContext.IncomeEntries.ToListAsync());
  }

  [Fact]
  public async Task ApprovePaid_RequiresAChargedAmountAboveZero()
  {
    var record = (await RegisterAsync()).Single();

    foreach (int? amount in new int?[] { null, 0, -5 })
    {
      var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
          _service.ApproveAsync(_admin.Id, record.Id, Approve(record, "Paid", chargedAmount: amount)));
      Assert.Contains("chargedAmount", exception.Errors.Keys);
    }

    Assert.Empty(await _dbContext.IncomeEntries.ToListAsync());
  }

  [Fact]
  public async Task Approve_CanCorrectServiceProfessionalCustomerAndDate_KeepingTheRegisteredValues()
  {
    var customer = await CreateCustomerAsync();
    var record = (await RegisterAsync(serviceIds: [_haircut.Id], customerName: "Nombre escrito")).Single();

    var approved = await _service.ApproveAsync(_admin.Id, record.Id, Approve(
        record, "Paid", chargedAmount: 15000, serviceId: _beard.Id, staffId: _otherBarber.Id,
        customerId: customer.Id, performedOn: _today.AddDays(-1)));

    // Registered values stay as the professional entered them.
    Assert.Equal((ServiceCatalogDefaults.CorteGeneralServiceId, "Corte", (Guid?)null, _barber.Id, _today, (Guid?)null),
        (approved.GeneralServiceId!.Value, approved.ServiceName, approved.ServiceId, approved.StaffProfileId, approved.PerformedOn, approved.CustomerId));
    Assert.Equal("Nombre escrito", approved.CustomerName);
    var approval = approved.Approval!;
    Assert.Equal((_beard.Id, "Barba", _otherBarber.Id, "Beto", customer.Id, _today.AddDays(-1)),
        (approval.ServiceId, approval.ServiceName, approval.StaffProfileId, approval.StaffName!, approval.CustomerId!.Value, approval.PerformedOn));
    Assert.Equal((_beardGeneral.Id, "Barba"), (approval.GeneralServiceId!.Value, approval.GeneralServiceName));
    Assert.Equal(_beardGeneral.Id, (await _dbContext.ServiceRecords.SingleAsync(item => item.Id == record.Id)).ApprovedGeneralServiceId);
    var income = Assert.Single(await _dbContext.IncomeEntries.ToListAsync());
    Assert.Equal((_beard.Id, _otherBarber.Id, 40), (income.ServiceId!.Value, income.StaffProfileId!.Value, income.BusinessPercentageSnapshot));
  }

  [Fact]
  public async Task Approve_WithARateOfAnotherGeneralService_IsAConflict_AndNothingIsCreated()
  {
    var record = (await RegisterAsync(serviceIds: [_haircut.Id])).Single();

    // The registered general service (Corte) is kept when none is sent; Barba's rate does not belong to it.
    await Assert.ThrowsAsync<ConflictException>(() => _service.ApproveAsync(_admin.Id, record.Id,
        Approve(record, "Paid", chargedAmount: 15000, serviceId: _beard.Id) with { GeneralServiceId = null }));
    await Assert.ThrowsAsync<ConflictException>(() => _service.ApproveAsync(_admin.Id, record.Id,
        Approve(record, "Paid", chargedAmount: 15000, serviceId: _beard.Id) with { GeneralServiceId = ServiceCatalogDefaults.CorteGeneralServiceId }));
    var unknown = await Assert.ThrowsAsync<ValidationProblemException>(() => _service.ApproveAsync(_admin.Id, record.Id,
        Approve(record, "Paid", chargedAmount: 15000) with { GeneralServiceId = Guid.NewGuid() }));
    Assert.Contains("generalServiceId", unknown.Errors.Keys);

    Assert.Empty(await _dbContext.IncomeEntries.ToListAsync());
    Assert.Equal(ServiceRecordStatus.Pending, (await _dbContext.ServiceRecords.AsNoTracking().SingleAsync(item => item.Id == record.Id)).Status);

    // The registered general service, kept implicitly, accepts its own rate.
    var approved = await _service.ApproveAsync(_admin.Id, record.Id,
        Approve(record, "Paid", chargedAmount: 25000) with { GeneralServiceId = null });
    Assert.Equal(ServiceCatalogDefaults.CorteGeneralServiceId, approved.Approval!.GeneralServiceId);
  }

  [Fact]
  public async Task APendingRecordFromBeforeGeneralServices_TakesTheGeneralServiceOfItsRate()
  {
    // Registered with a rate, as every record before ADR-0016 B4.
    var legacy = new ServiceRecord(Guid.NewGuid(), 0, null, _barber.Id, _beard.Id, _beard.Name, _today, null, null, null, _barberUser.Id, DateTime.UtcNow);
    _dbContext.ServiceRecords.Add(legacy);
    await _dbContext.SaveChangesAsync();

    var view = await _service.GetByIdAsync(legacy.Id);
    Assert.Equal((_beard.Id, _beardGeneral.Id, "Barba"), (view.ServiceId!.Value, view.GeneralServiceId!.Value, view.GeneralServiceName));

    await Assert.ThrowsAsync<ConflictException>(() => _service.ApproveAsync(_admin.Id, legacy.Id,
        Approve(view, "Paid", chargedAmount: 25000, serviceId: _haircut.Id) with { GeneralServiceId = null }));

    var approved = await _service.ApproveAsync(_admin.Id, legacy.Id, Approve(view, "Courtesy") with { GeneralServiceId = null });
    Assert.Equal((_beard.Id, _beardGeneral.Id), (approved.Approval!.ServiceId, approved.Approval.GeneralServiceId!.Value));
  }

  [Fact]
  public async Task Options_LabelEachRateWithItsGeneralService_AndListTheGeneralServices()
  {
    var options = await _service.GetOptionsAsync();

    Assert.Contains(options.Services, rate => rate.Id == _beard.Id && rate.GeneralServiceId == _beardGeneral.Id && rate.GeneralServiceName == "Barba");
    Assert.Contains(options.Services, rate => rate.Id == _haircut.Id && rate.GeneralServiceName == "Corte");
    Assert.Contains(options.GeneralServices, general => general.Id == _beardGeneral.Id && general.CategoryName == "Barbería");

    var catalog = await _service.GetCatalogAsync();
    Assert.Equal(
        [ServiceCatalogDefaults.CorteGeneralServiceId, _beardGeneral.Id],
        catalog.Select(item => item.Id));
  }

  [Fact]
  public async Task PlansAndRewardOptions_LabelEachRateWithItsGeneralService()
  {
    var plan = await _plans.CreateAsync(new MembershipPlanUpsertRequest(
        "Oro", null, 250000, "Pool", 10, 90, Terms, null, [new(_haircut.Id, null), new(_beard.Id, null)], []));
    Assert.Equal(
        [("Barba", "Barba"), ("Corte", "Corte")],
        plan.Services.OrderBy(service => service.ServiceName).Select(service => (service.ServiceName, service.GeneralServiceName!)));

    var rewardOptions = await new Barbershop.Infrastructure.Loyalty.LoyaltyRedemptionService(_dbContext, TimeProvider.System).GetServiceOptionsAsync();
    Assert.Contains(rewardOptions, option => option.Id == _beard.Id && option.GeneralServiceId == _beardGeneral.Id && option.GeneralServiceName == "Barba");
    Assert.Contains(rewardOptions, option => option.Id == _haircut.Id && option.GeneralServiceName == "Corte");
  }

  [Fact]
  public async Task AppointmentOptions_PrefillTheBookedService_AndListWhatIsAlreadyRegistered()
  {
    var startsAt = DateTime.UtcNow.AddHours(-3);
    var booked = Appointment.ForService(
        _barber.Id, "Cliente", startsAt, new AppointmentServiceReservation(_beardGeneral.Id, "Barba", 20),
        AppointmentStatus.Confirmed, AppointmentSource.Manual, DateTime.UtcNow);
    var legacy = await CreateAppointmentAsync(_barber, AppointmentStatus.Confirmed);
    _dbContext.Appointments.Add(booked);
    // An older record of the legacy appointment registered a rate: it counts as its general service.
    _dbContext.ServiceRecords.Add(new ServiceRecord(
        Guid.NewGuid(), 0, legacy.Id, _barber.Id, _haircut.Id, _haircut.Name, _today, null, null, null, _barberUser.Id, DateTime.UtcNow));
    await _dbContext.SaveChangesAsync();

    var options = await _service.GetAppointmentsAsync(_barberUser.Id);

    var bookedOption = Assert.Single(options, option => option.Id == booked.Id);
    Assert.Equal((_beardGeneral.Id, "Barba"), (bookedOption.GeneralServiceId!.Value, bookedOption.GeneralServiceName));
    var legacyOption = Assert.Single(options, option => option.Id == legacy.Id);
    Assert.Null(legacyOption.GeneralServiceId);
    Assert.Equal([ServiceCatalogDefaults.CorteGeneralServiceId], legacyOption.RegisteredGeneralServiceIds);

    // Registering Corte again on it is a conflict, as with any other duplicate.
    await Assert.ThrowsAsync<ConflictException>(() =>
        RegisterAsync(appointmentId: legacy.Id, performedOn: null, serviceIds: [_haircut.Id]));
  }

  [Fact]
  public async Task TheIncomeOfAConfirmedRecord_CannotBeEditedOrDeletedFromIncome_AndManualIncomeIsUnchanged()
  {
    var record = (await RegisterAsync()).Single();
    var approved = await _service.ApproveAsync(_admin.Id, record.Id, Approve(record, "Paid", chargedAmount: 25000));
    var incomeId = approved.Approval!.IncomeEntryId!.Value;

    var listed = Assert.Single((await _income.GetAsync(new IncomeListQuery(_today, _today))).Items);
    Assert.Equal(record.Id, listed.ServiceRecordId);
    await Assert.ThrowsAsync<ConflictException>(() => _income.UpdateAsync(
        incomeId, new IncomeEntryUpdateRequest(_haircut.Id, _barber.Id, 1, false, _today)));
    await Assert.ThrowsAsync<ConflictException>(() => _income.SoftDeleteAsync(incomeId));
    Assert.Equal(25000, (await _dbContext.IncomeEntries.SingleAsync()).Amount);

    // A manual income behaves as before.
    var manual = await _income.CreateAsync(_admin.Id, new IncomeEntryCreateRequest(_haircut.Id, _barber.Id, 20000, false, _today));
    Assert.Null(manual.ServiceRecordId);
    await _income.UpdateAsync(manual.Id, new IncomeEntryUpdateRequest(_haircut.Id, _barber.Id, 21000, false, _today));
    await _income.SoftDeleteAsync(manual.Id);
  }

  // ── Membership consumption, courtesy, reward ───────────────────────────────────────────────

  [Fact]
  public async Task ApproveMembershipConsumption_UsesTheLedgerOnce_WithoutIncomeOrPoints()
  {
    var (customer, membership) = await SellActiveMembershipAsync();
    var record = (await RegisterAsync()).Single();
    var request = Approve(record, "MembershipConsumption", customerId: customer.Id, membershipId: membership.Id);

    var approved = await _service.ApproveAsync(_admin.Id, record.Id, request);
    await _service.ApproveAsync(_admin.Id, record.Id, request);

    var consumption = Assert.Single(await _dbContext.MembershipConsumptions.ToListAsync());
    Assert.Equal((record.Id, _haircut.Id, 1, _barber.Id), (consumption.IdempotencyKey, consumption.ServiceId, consumption.Quantity, consumption.StaffProfileId!.Value));
    Assert.Equal(consumption.Id, approved.Approval!.MembershipConsumptionId);
    Assert.Equal(membership.Id, approved.Approval.CustomerMembershipId);
    Assert.False(approved.Approval.AccruesLoyaltyPoints);
    Assert.Empty(await _dbContext.IncomeEntries.ToListAsync());
    var after = await _memberships.GetByIdAsync(membership.Id);
    Assert.Equal(9, after.Allowances.Single().AvailableQuantity);

    // The consumption belongs to the record: it cannot be reversed from the membership.
    await Assert.ThrowsAsync<ConflictException>(() =>
        _ledger.ReverseAsync(_admin.Id, membership.Id, consumption.Id, new MembershipConsumptionReverseRequest("Error de registro")));
  }

  [Fact]
  public async Task ApproveMembershipConsumption_RespectsTheLedgerAndTheMembershipsOwner()
  {
    var (_, membership) = await SellActiveMembershipAsync();
    var otherCustomer = await CreateCustomerAsync("Otra Persona");
    var record = (await RegisterAsync()).Single();

    var notOwner = await Assert.ThrowsAsync<ValidationProblemException>(() => _service.ApproveAsync(
        _admin.Id, record.Id, Approve(record, "MembershipConsumption", customerId: otherCustomer.Id, membershipId: membership.Id)));
    Assert.Contains("customerMembershipId", notOwner.Errors.Keys);

    // A service the membership does not include is rejected by the ledger, and nothing is confirmed.
    var dye = new Service("Tinte", 40000, DateTime.UtcNow, 30);
    _dbContext.Services.Add(dye);
    await _dbContext.SaveChangesAsync();
    var owner = (await _dbContext.CustomerMemberships.SingleAsync()).CustomerId;
    var notIncluded = await Assert.ThrowsAsync<ValidationProblemException>(() => _service.ApproveAsync(
        _admin.Id, record.Id, Approve(record, "MembershipConsumption", serviceId: dye.Id, customerId: owner, membershipId: membership.Id)));
    Assert.Contains("serviceId", notIncluded.Errors.Keys);
    Assert.Equal("Pending", (await _service.GetByIdAsync(record.Id)).Status);
  }

  [Fact]
  public async Task ApproveCourtesy_CreatesNoIncomeNoConsumptionAndNoPoints()
  {
    var record = (await RegisterAsync()).Single();

    var approved = await _service.ApproveAsync(_admin.Id, record.Id, Approve(record, "Courtesy"));

    Assert.Equal("Courtesy", approved.Approval!.OperationType);
    Assert.Null(approved.Approval.IncomeEntryId);
    Assert.Null(approved.Approval.ChargedAmount);
    Assert.Empty(await _dbContext.IncomeEntries.ToListAsync());
    Assert.Empty(await _dbContext.MembershipConsumptions.ToListAsync());

    var charged = (await RegisterAsync(serviceIds: [_beard.Id])).Single();
    var withAmount = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.ApproveAsync(_admin.Id, charged.Id, Approve(charged, "Courtesy", chargedAmount: 1000)));
    Assert.Contains("chargedAmount", withAmount.Errors.Keys);
  }

  [Fact]
  public async Task ALoyaltyReward_NeedsAnApprovedRedemption_AndACustomer()
  {
    // Since stage 5 (ADR-0015 R13) a reward is confirmable, but only delivering an approved redemption; the full
    // flow is in LoyaltyRedemptionServiceTests.
    var record = (await RegisterAsync()).Single();

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.ApproveAsync(_admin.Id, record.Id, Approve(record, "LoyaltyReward")));

    Assert.Contains("loyaltyRedemptionId", exception.Errors.Keys);
    Assert.Equal("Pending", (await _service.GetByIdAsync(record.Id)).Status);
  }

  // ── Points ─────────────────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task Points_AreRecordedAsASnapshot_ForAPaidServiceOfAnIdentifiedCustomer()
  {
    await EnableLoyaltyAsync();
    var customer = await CreateCustomerAsync();
    var record = (await RegisterAsync()).Single();

    var approved = await _service.ApproveAsync(_admin.Id, record.Id,
        Approve(record, "Paid", chargedAmount: 25000, customerId: customer.Id, accrues: true));

    Assert.True(approved.Approval!.AccruesLoyaltyPoints);
    Assert.Equal(10, approved.Approval.LoyaltyPointsSnapshot);

    // The snapshot does not follow later changes of the service.
    _haircut.SetLoyaltyPoints(50, DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();
    Assert.Equal(10, (await _service.GetByIdAsync(record.Id)).Approval!.LoyaltyPointsSnapshot);
  }

  [Fact]
  public async Task ApprovingAPaidServiceThatAccrues_CreditsItsSnapshotOnce_WithTheValidityOfTheAccrualDay()
  {
    await EnableLoyaltyAsync();
    var customer = await CreateCustomerAsync();
    var record = (await RegisterAsync()).Single();
    var request = Approve(record, "Paid", chargedAmount: 25000, customerId: customer.Id, accrues: true);

    var approved = await _service.ApproveAsync(_admin.Id, record.Id, request);
    var retried = await _service.ApproveAsync(_admin.Id, record.Id, request);

    Assert.Equal(("Credited", "Credited"), (approved.LoyaltyAccrualStatus, retried.LoyaltyAccrualStatus));
    var lot = await _dbContext.LoyaltyLots.SingleAsync();
    Assert.Equal((customer.Id, LoyaltySourceType.ServiceRecord, record.Id, 10, 10), (lot.CustomerId, lot.SourceType, lot.SourceId, lot.InitialPoints, lot.RemainingPoints));
    var accruedOn = DateOnly.FromDateTime(BogotaClock.ToLocal(lot.AccruedAt));
    Assert.Equal((accruedOn, 365, accruedOn.AddDays(364)), (lot.AccruedOn, lot.ValidityDays, lot.ExpiresOn));
    var movement = await _dbContext.LoyaltyMovements.SingleAsync();
    Assert.Equal((LoyaltyMovementType.Accrual, 10, lot.Id, _admin.Id), (movement.Type, movement.Points, movement.LotId!.Value, movement.PerformedByUserId!.Value));
  }

  [Fact]
  public async Task ARecordApprovedBeforeTheWallet_IsShownAsNotCredited_AndIsNeverCreditedRetroactively()
  {
    await EnableLoyaltyAsync();
    var customer = await CreateCustomerAsync();
    var record = (await RegisterAsync()).Single();
    await _service.ApproveAsync(_admin.Id, record.Id, Approve(record, "Paid", chargedAmount: 25000, customerId: customer.Id, accrues: true));

    // As if it had been approved before stage 4: marked to accrue, but no lot.
    _dbContext.LoyaltyMovements.RemoveRange(_dbContext.LoyaltyMovements);
    _dbContext.LoyaltyLots.RemoveRange(_dbContext.LoyaltyLots);
    await _dbContext.SaveChangesAsync();

    Assert.Equal("PredatesWallet", (await _service.GetByIdAsync(record.Id)).LoyaltyAccrualStatus);
    Assert.Equal(0, await _dbContext.LoyaltyLots.CountAsync());
  }

  [Fact]
  public async Task Points_AreRefused_ForAnythingButAnEligiblePaidService()
  {
    await EnableLoyaltyAsync();
    var customer = await CreateCustomerAsync();
    var (owner, membership) = await SellActiveMembershipAsync();
    var record = (await RegisterAsync()).Single();
    var beardRecord = (await RegisterAsync(serviceIds: [_beard.Id])).Single();

    async Task AssertRefusedAsync(ServiceRecordView target, ServiceRecordApproveRequest request)
    {
      var exception = await Assert.ThrowsAsync<ValidationProblemException>(() => _service.ApproveAsync(_admin.Id, target.Id, request));
      Assert.Contains("accruesLoyaltyPoints", exception.Errors.Keys);
    }

    await AssertRefusedAsync(record, Approve(record, "Paid", chargedAmount: 25000, accrues: true));                       // no customer
    await AssertRefusedAsync(beardRecord, Approve(beardRecord, "Paid", chargedAmount: 15000, customerId: customer.Id, accrues: true)); // 0 points
    await AssertRefusedAsync(record, Approve(record, "Courtesy", customerId: customer.Id, accrues: true));                // courtesy / free
    await AssertRefusedAsync(record, Approve(record, "MembershipConsumption", customerId: owner.Id, membershipId: membership.Id, accrues: true));

    await DisableLoyaltyAsync();
    await AssertRefusedAsync(record, Approve(record, "Paid", chargedAmount: 25000, customerId: customer.Id, accrues: true)); // program off

    Assert.Empty(await _dbContext.IncomeEntries.ToListAsync());
    Assert.Empty(await _dbContext.MembershipConsumptions.ToListAsync());
  }

  // ── Rejection and review integrity ─────────────────────────────────────────────────────────

  [Fact]
  public async Task Reject_RequiresAReason_IsFinal_AndIsAudited()
  {
    var record = (await RegisterAsync()).Single();

    var noReason = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.RejectAsync(_admin.Id, record.Id, new ServiceRecordRejectRequest(record.Version, " ")));
    Assert.Contains("reason", noReason.Errors.Keys);

    var rejected = await _service.RejectAsync(_admin.Id, record.Id, new ServiceRecordRejectRequest(record.Version, "Servicio duplicado"));
    var retried = await _service.RejectAsync(_admin.Id, record.Id, new ServiceRecordRejectRequest(record.Version, "Servicio duplicado"));

    Assert.Equal(("Rejected", "Servicio duplicado", "Admin Principal"), (rejected.Status, rejected.RejectionReason!, rejected.RejectedByName!));
    Assert.Equal(rejected.RejectedAt, retried.RejectedAt);
    await Assert.ThrowsAsync<ConflictException>(() => _service.ApproveAsync(_admin.Id, record.Id, Approve(record, "Courtesy")));
    Assert.Empty(await _dbContext.IncomeEntries.ToListAsync());
  }

  [Fact]
  public async Task Review_WithAStaleVersion_IsAConflict()
  {
    var record = (await RegisterAsync()).Single();

    await Assert.ThrowsAsync<ConflictException>(() =>
        _service.ApproveAsync(_admin.Id, record.Id, Approve(record, "Courtesy") with { Version = record.Version + 1 }));
    await Assert.ThrowsAsync<ConflictException>(() =>
        _service.RejectAsync(_admin.Id, record.Id, new ServiceRecordRejectRequest(record.Version + 1, "Motivo")));
    var missing = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.ApproveAsync(_admin.Id, record.Id, Approve(record, "Courtesy") with { Version = null }));
    Assert.Contains("version", missing.Errors.Keys);
  }

  [Fact]
  public async Task AnAdministratorWhoIsAlsoAProfessional_CanApproveTheirOwnRecord_AndBothStepsAreAudited()
  {
    var record = (await RegisterAsync()).Single();

    // The barber's own user approves it (as an admin who also works as a professional would).
    var approved = await _service.ApproveAsync(_barberUser.Id, record.Id, Approve(record, "Paid", chargedAmount: 25000));

    var stored = await _dbContext.ServiceRecords.AsNoTracking().SingleAsync();
    Assert.Equal((_barberUser.Id, _barberUser.Id), (stored.RegisteredByUserId, stored.ApprovedByUserId!.Value));
    Assert.Equal(("Alex Barbero", "Alex Barbero"), (approved.RegisteredByName!, approved.Approval!.ApprovedByName!));
  }

  [Fact]
  public async Task Inbox_ListsPendingOldestFirst_AndHistoryByStatus()
  {
    var first = (await RegisterAsync()).Single();
    var second = (await RegisterAsync(serviceIds: [_beard.Id])).Single();
    await _service.RejectAsync(_admin.Id, second.Id, new ServiceRecordRejectRequest(second.Version, "No aplica"));

    Assert.Equal([first.Id], (await _service.ListAsync(new ServiceRecordListQuery())).Items.Select(record => record.Id));
    Assert.Equal([second.Id], (await _service.ListAsync(new ServiceRecordListQuery("Rejected"))).Items.Select(record => record.Id));
    Assert.Empty((await _service.ListAsync(new ServiceRecordListQuery("Approved"))).Items);
    await Assert.ThrowsAsync<ValidationProblemException>(() => _service.ListAsync(new ServiceRecordListQuery("Whatever")));
  }

  [Fact]
  public async Task Inbox_IsPaginatedWithoutRepeats_AndFiltersByGeneralServiceStaffAndDate()
  {
    // One registration of two services (same registration instant, told apart by position), then three more.
    var batch = await RegisterAsync(serviceIds: [_haircut.Id, _beard.Id]);
    foreach (var rate in new[] { _haircut.Id, _beard.Id, _haircut.Id })
    {
      await RegisterAsync(serviceIds: [rate]);
    }

    var older = (await RegisterAsync(performedOn: _today.AddDays(-3), serviceIds: [_beard.Id])).Single();

    var seen = new List<Guid>();
    for (var page = 1; page <= 3; page++)
    {
      var result = await _service.ListAsync(new ServiceRecordListQuery(Page: page, PageSize: 2));
      Assert.Equal(6, result.TotalCount);
      seen.AddRange(result.Items.Select(record => record.Id));
    }

    Assert.Equal(6, seen.Distinct().Count());
    Assert.Equal(batch.Select(record => record.Id), seen.Take(2));

    var beard = await _service.ListAsync(new ServiceRecordListQuery(GeneralServiceId: _beardGeneral.Id));
    Assert.Equal(3, beard.TotalCount);
    Assert.All(beard.Items, record => Assert.Equal(_beardGeneral.Id, record.GeneralServiceId));
    Assert.Equal(older.Id, Assert.Single((await _service.ListAsync(new ServiceRecordListQuery(To: _today.AddDays(-1)))).Items).Id);
    Assert.Equal(5, (await _service.ListAsync(new ServiceRecordListQuery(From: _today, StaffProfileId: older.StaffProfileId))).TotalCount);
    Assert.Equal(0, (await _service.ListAsync(new ServiceRecordListQuery(StaffProfileId: Guid.NewGuid()))).TotalCount);

    // Once approved with another general service, the approved one is what counts.
    var corrected = await _service.ApproveAsync(_admin.Id, older.Id, Approve(older, "Courtesy", serviceId: _haircut.Id));
    Assert.Equal(ServiceCatalogDefaults.CorteGeneralServiceId, corrected.Approval!.GeneralServiceId);
    Assert.Equal(older.Id, Assert.Single((await _service.ListAsync(
        new ServiceRecordListQuery("Approved", GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId))).Items).Id);
    Assert.Empty((await _service.ListAsync(new ServiceRecordListQuery("Approved", GeneralServiceId: _beardGeneral.Id))).Items);
    await Assert.ThrowsAsync<ValidationProblemException>(() => _service.ListAsync(new ServiceRecordListQuery(PageSize: 0)));
  }

  // ── Helpers ────────────────────────────────────────────────────────────────────────────────

  private Task<IReadOnlyList<ServiceRecordView>> RegisterAsync(
      Guid? appointmentId = null,
      DateOnly? performedOn = default,
      IReadOnlyList<Guid>? serviceIds = null,
      string? customerName = null,
      Guid? key = null)
      => _service.RegisterAsync(_barberUser.Id, new ServiceRecordRegisterRequest(
          appointmentId,
          performedOn == default && appointmentId is null ? _today : performedOn,
          customerName,
          (serviceIds ?? [_haircut.Id]).Select(GeneralOf).ToArray(),
          null,
          key ?? Guid.NewGuid()));

  /// <summary>The tests name rates for readability; registration takes the general service of each one.</summary>
  private Guid GeneralOf(Guid rateId) => _dbContext.Services.AsNoTracking().Single(rate => rate.Id == rateId).GeneralServiceId;

  /// <summary>The rate a test confirms by default: the one of the registered general service (Corte or Barba).</summary>
  private Guid DefaultRateOf(ServiceRecordView record)
      => record.GeneralServiceId == _beardGeneral.Id ? _beard.Id : _haircut.Id;

  private ServiceRecordApproveRequest Approve(
      ServiceRecordView record,
      string operationType,
      int? chargedAmount = null,
      Guid? serviceId = null,
      Guid? staffId = null,
      Guid? customerId = null,
      DateOnly? performedOn = null,
      Guid? membershipId = null,
      bool accrues = false)
      => new(record.Version, operationType, serviceId ?? DefaultRateOf(record), staffId ?? record.StaffProfileId, customerId,
          performedOn ?? record.PerformedOn, chargedAmount, membershipId, accrues, null,
          GeneralServiceId: GeneralOf(serviceId ?? DefaultRateOf(record)));

  private async Task<Appointment> CreateAppointmentAsync(StaffProfile staff, AppointmentStatus status, User? customerUser = null)
  {
    var startsAt = DateTime.UtcNow.AddHours(-3);
    var appointment = new Appointment(
        staff.Id, customerUser?.FullName ?? "Cliente Cita", startsAt, startsAt.AddMinutes(30), status,
        AppointmentSource.Manual, DateTime.UtcNow, customerUser?.Id);
    _dbContext.Appointments.Add(appointment);
    await _dbContext.SaveChangesAsync();
    return appointment;
  }

  private async Task<Appointment> CreateAppointmentAtAsync(DateTime startsAtUtc)
  {
    var appointment = new Appointment(
        _barber.Id, "Cliente Cita", startsAtUtc, startsAtUtc.AddMinutes(30), AppointmentStatus.Confirmed,
        AppointmentSource.Manual, startsAtUtc.AddDays(-1));
    _dbContext.Appointments.Add(appointment);
    await _dbContext.SaveChangesAsync();
    return appointment;
  }

  private async Task<CustomerDetailView> CreateCustomerAsync(string name = "Carlos Pérez", User? linkedUser = null)
  {
    var created = await _customers.CreateAsync(_admin.Id, new CustomerCreateRequest(name, "3001234567", $"{Guid.NewGuid():N}@example.com"));
    if (linkedUser is not null)
    {
      var profile = await _dbContext.Customers.SingleAsync(customer => customer.Id == created.Id);
      profile.LinkAccount(linkedUser.Id, _admin.Id, DateTime.UtcNow);
      await _dbContext.SaveChangesAsync();
    }

    return created;
  }

  private async Task<(CustomerDetailView Customer, CustomerMembershipDetailView Membership)> SellActiveMembershipAsync()
  {
    var plan = await _plans.CreateAsync(new MembershipPlanUpsertRequest(
        "Oro", "Diez servicios", 250000, "Pool", 10, 90, Terms, null, [new(_haircut.Id, null), new(_beard.Id, null)], []));
    var customer = await CreateCustomerAsync("Dueño Membresía");
    var membership = await _memberships.SellAsync(_admin.Id, customer.Id, new MembershipSaleRequest(plan.Id, _today, ActivateNow: true));
    return (customer, membership);
  }

  private async Task EnableLoyaltyAsync()
  {
    var settings = await _dbContext.LoyaltySettings.SingleAsync();
    settings.Update(true, 365, _admin.Id, DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();
  }

  private async Task DisableLoyaltyAsync()
  {
    var settings = await _dbContext.LoyaltySettings.SingleAsync();
    settings.Update(false, 365, _admin.Id, DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();
  }
}
