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
    _haircut = new Service("Corte", 25000, DateTime.UtcNow, 30);
    _haircut.SetLoyaltyPoints(10, DateTime.UtcNow);
    _beard = new Service("Barba", 15000, DateTime.UtcNow, 40);
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
    Assert.Contains("serviceIds", repeated.Errors.Keys);

    _beard.Deactivate(DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();
    var inactive = await Assert.ThrowsAsync<ValidationProblemException>(() => RegisterAsync(serviceIds: [_beard.Id]));
    Assert.Contains("serviceIds[0]", inactive.Errors.Keys);

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
    Assert.Equal((_haircut.Id, _barber.Id, _today, (Guid?)null), (approved.ServiceId, approved.StaffProfileId, approved.PerformedOn, approved.CustomerId));
    Assert.Equal("Nombre escrito", approved.CustomerName);
    var approval = approved.Approval!;
    Assert.Equal((_beard.Id, "Barba", _otherBarber.Id, "Beto", customer.Id, _today.AddDays(-1)),
        (approval.ServiceId, approval.ServiceName, approval.StaffProfileId, approval.StaffName!, approval.CustomerId!.Value, approval.PerformedOn));
    var income = Assert.Single(await _dbContext.IncomeEntries.ToListAsync());
    Assert.Equal((_beard.Id, _otherBarber.Id, 40), (income.ServiceId!.Value, income.StaffProfileId!.Value, income.BusinessPercentageSnapshot));
  }

  [Fact]
  public async Task TheIncomeOfAConfirmedRecord_CannotBeEditedOrDeletedFromIncome_AndManualIncomeIsUnchanged()
  {
    var record = (await RegisterAsync()).Single();
    var approved = await _service.ApproveAsync(_admin.Id, record.Id, Approve(record, "Paid", chargedAmount: 25000));
    var incomeId = approved.Approval!.IncomeEntryId!.Value;

    var listed = Assert.Single(await _income.GetAsync(_today, null));
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
  public async Task ALoyaltyReward_CannotBeConfirmedYet()
  {
    var record = (await RegisterAsync()).Single();

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.ApproveAsync(_admin.Id, record.Id, Approve(record, "LoyaltyReward")));

    Assert.Contains("operationType", exception.Errors.Keys);
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

    Assert.Equal([first.Id], (await _service.ListAsync(null, null, null, null)).Select(record => record.Id));
    Assert.Equal([second.Id], (await _service.ListAsync("Rejected", null, null, null)).Select(record => record.Id));
    Assert.Empty(await _service.ListAsync("Approved", null, null, null));
    await Assert.ThrowsAsync<ValidationProblemException>(() => _service.ListAsync("Whatever", null, null, null));
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
          serviceIds ?? [_haircut.Id],
          null,
          key ?? Guid.NewGuid()));

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
      => new(record.Version, operationType, serviceId ?? record.ServiceId, staffId ?? record.StaffProfileId, customerId,
          performedOn ?? record.PerformedOn, chargedAmount, membershipId, accrues, null);

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
