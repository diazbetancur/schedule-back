using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Customers.Claims;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Application.ServiceRecords;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Common;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Customers;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.ServiceRecords;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Barbershop.Tests.Infrastructure;

namespace Barbershop.Tests.Features.Customers;

/// <summary>Own profile, claims and their review. Locks and real concurrency are in CustomerClaimsHttpIntegrationTests.</summary>
public sealed class CustomerClaimsServiceTests : IDisposable
{
  private const string Terms = "La membresía es personal e intransferible.";

  private readonly DateOnly _today = DateOnly.FromDateTime(BogotaClock.ToLocal(DateTime.UtcNow));
  private readonly AppDbContext _dbContext;
  private readonly CustomerManagementService _customers;
  private readonly CustomerClaimsService _claims;
  private readonly MembershipSalesService _memberships;
  private readonly MembershipPlanManagementService _plans;
  private readonly Role _customerRole;
  private readonly User _admin;
  private readonly Service _haircut;

  public CustomerClaimsServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options;
    _dbContext = new AppDbContext(options);
    _dbContext.Database.EnsureCreated();
    _customers = new CustomerManagementService(_dbContext, TimeProvider.System);
    _claims = new CustomerClaimsService(_dbContext, _customers, TimeProvider.System);
    _memberships = new MembershipSalesService(_dbContext, TimeProvider.System);
    _plans = new MembershipPlanManagementService(_dbContext, TimeProvider.System, new NullCurrentUserAccessor());

    _customerRole = new Role(RoleNames.Customer, isSystemRole: true);
    _admin = new User("Admin Principal", "admin@example.com", "hash", DateTime.UtcNow);
    _haircut = new Service("Corte", 25000, DateTime.UtcNow, 30);
    _dbContext.Roles.Add(_customerRole);
    _dbContext.Users.Add(_admin);
    _dbContext.Services.Add(_haircut);
    _dbContext.SaveChanges();
  }

  public void Dispose() => _dbContext.Dispose();

  // ── New customer ───────────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task CreateOwnProfile_LinksItToTheOwnAccount_AndARetryReturnsTheSameProfile()
  {
    var user = await AddCustomerAccountAsync("Carla Cliente");

    var before = await _claims.GetStatusAsync(user.Id);
    Assert.Null(before.Profile);
    Assert.Equal(("Carla Cliente", user.Email), (before.AccountFullName, before.AccountEmail));

    var created = await _claims.CreateOwnProfileAsync(user.Id, new OwnCustomerProfileCreateRequest("Carla Cliente", "300 123 4567", user.Email));
    var retried = await _claims.CreateOwnProfileAsync(user.Id, new OwnCustomerProfileCreateRequest("Carla Cliente", "300 123 4567", user.Email));

    Assert.Equal(created.Id, retried.Id);
    var profile = await _dbContext.Customers.SingleAsync();
    Assert.Equal((user.Id, user.Id, user.Id), (profile.UserId!.Value, profile.LinkedByUserId!.Value, profile.CreatedByUserId));
    var events = await _dbContext.CustomerEvents.Where(customerEvent => customerEvent.CustomerId == profile.Id).Select(customerEvent => customerEvent.Type).ToListAsync();
    Assert.Equal([CustomerEventType.Created, CustomerEventType.AccountLinked], events.Order());
    Assert.Equal(created.Id, (await _claims.GetStatusAsync(user.Id)).Profile!.Id);
  }

  [Fact]
  public async Task CreateOwnProfile_ValidatesContactData_AndIsBlockedWhileAClaimIsPending()
  {
    var user = await AddCustomerAccountAsync("Carla Cliente");

    var invalid = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _claims.CreateOwnProfileAsync(user.Id, new OwnCustomerProfileCreateRequest("Carla", "12", "no-es-correo")));
    Assert.Contains("phone", invalid.Errors.Keys);
    Assert.Contains("email", invalid.Errors.Keys);

    await SubmitClaimAsync(user);
    await Assert.ThrowsAsync<ConflictException>(() =>
        _claims.CreateOwnProfileAsync(user.Id, new OwnCustomerProfileCreateRequest("Carla", "3001234567", user.Email)));
    Assert.Empty(await _dbContext.Customers.ToListAsync());
  }

  [Fact]
  public async Task CreateOwnProfile_NeverLinksAHistoricalProfileWithTheSameData()
  {
    var historical = await _customers.CreateAsync(_admin.Id, new CustomerCreateRequest("Carla Cliente", "3001234567", "carla@example.com"));
    var user = await AddCustomerAccountAsync("Carla Cliente", "carla@example.com");

    var created = await _claims.CreateOwnProfileAsync(user.Id, new OwnCustomerProfileCreateRequest("Carla Cliente", "3001234567", "carla@example.com"));

    Assert.NotEqual(historical.Id, created.Id);
    Assert.Null((await _dbContext.Customers.SingleAsync(customer => customer.Id == historical.Id)).UserId);
  }

  // ── Claims ─────────────────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task SubmitClaim_IsPending_IdempotentByKey_AndAPendingDuplicateIsRefused()
  {
    var user = await AddCustomerAccountAsync("Carla Cliente");
    var key = Guid.NewGuid();

    var claim = await SubmitClaimAsync(user, key);
    var retried = await SubmitClaimAsync(user, key);

    Assert.Equal(("Pending", claim.Id), (claim.Status, retried.Id));
    await Assert.ThrowsAsync<ConflictException>(() => SubmitClaimAsync(user));
    Assert.Single(await _dbContext.CustomerClaims.ToListAsync());
    var status = await _claims.GetStatusAsync(user.Id);
    Assert.Equal(claim.Id, status.PendingClaim!.Id);
    Assert.Null(status.Profile);
  }

  [Fact]
  public async Task SubmitClaim_IsRefused_ForInactiveAccounts_AccountsWithAProfile_AndNonCustomers()
  {
    var inactive = await AddCustomerAccountAsync("Inactiva");
    inactive.Deactivate(DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();
    await Assert.ThrowsAsync<ConflictException>(() => SubmitClaimAsync(inactive));

    var withProfile = await AddCustomerAccountAsync("Con Perfil");
    await _claims.CreateOwnProfileAsync(withProfile.Id, new OwnCustomerProfileCreateRequest("Con Perfil", "3001234567", withProfile.Email));
    await Assert.ThrowsAsync<ConflictException>(() => SubmitClaimAsync(withProfile));

    _dbContext.Users.Add(new User("Sin Rol", "sinrol@example.com", "hash", DateTime.UtcNow));
    await _dbContext.SaveChangesAsync();
    var noRole = await _dbContext.Users.SingleAsync(candidate => candidate.Email == "sinrol@example.com");
    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SubmitClaimAsync(noRole));

    Assert.Empty(await _dbContext.CustomerClaims.ToListAsync());
  }

  // ── Review ─────────────────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task Approve_LinksTheHistoricalProfile_KeepingItsMembershipHistory_WithFullTraceability()
  {
    var (historical, membership) = await HistoricalCustomerWithMembershipAsync();
    var user = await AddCustomerAccountAsync("Carla Cliente");
    var claim = await SubmitClaimAsync(user);

    // Before approval the customer sees no profile and no memberships.
    var myMemberships = new MyMembershipsService(_dbContext, _memberships);
    Assert.Empty(await myMemberships.GetMineAsync(user.Id));

    var view = await _claims.GetByIdAsync(claim.Id);
    var approved = await _claims.ApproveAsync(_admin.Id, claim.Id,
        new CustomerClaimApproveRequest(view.Version, historical.Id, true, "Cédula verificada en caja"));

    Assert.Equal(("Approved", historical.Id, "Admin Principal"), (approved.Status, approved.ApprovedCustomerId!.Value, approved.DecidedByName!));
    var profile = await _dbContext.Customers.SingleAsync(customer => customer.Id == historical.Id);
    Assert.Equal((user.Id, _admin.Id), (profile.UserId!.Value, profile.LinkedByUserId!.Value));
    Assert.Contains(await _dbContext.CustomerEvents.ToListAsync(), customerEvent =>
        customerEvent.CustomerId == historical.Id && customerEvent.Type == CustomerEventType.AccountLinked && customerEvent.UserId == user.Id);
    Assert.Equal(historical.Id, (await _claims.GetStatusAsync(user.Id)).Profile!.Id);
    Assert.Equal([membership.Id], (await myMemberships.GetMineAsync(user.Id)).Select(item => item.Id));
  }

  [Fact]
  public async Task AdminLinksOutsideTheClaim_AreRefusedWhileItIsPending_SoTheAccountNeverHasAProfileAndAPendingClaim()
  {
    var historical = await _customers.CreateAsync(_admin.Id, new CustomerCreateRequest("Carla", "3001234567", "carla@example.com"));
    var user = await AddCustomerAccountAsync("Carla");
    var claim = await _claims.GetByIdAsync((await SubmitClaimAsync(user)).Id);

    await Assert.ThrowsAsync<ConflictException>(() =>
        _customers.LinkAccountAsync(_admin.Id, historical.Id, new CustomerLinkAccountRequest(user.Id, true)));
    await Assert.ThrowsAsync<ConflictException>(() =>
        _customers.CreateFromAccountAsync(_admin.Id, new CustomerCreateFromAccountRequest(user.Id, "3001234567", true)));
    Assert.Null((await _dbContext.Customers.SingleAsync()).UserId);

    // The pending claim itself is the way: approving it links.
    var approved = await _claims.ApproveAsync(_admin.Id, claim.Id, new CustomerClaimApproveRequest(claim.Version, historical.Id, true, null));
    Assert.Equal(("Approved", user.Id), (approved.Status, (await _dbContext.Customers.SingleAsync()).UserId!.Value));
  }

  [Fact]
  public async Task Approve_RequiresInPersonVerification_AndKeepsTheClaimPending()
  {
    var historical = await _customers.CreateAsync(_admin.Id, new CustomerCreateRequest("Carla", "3001234567", "carla@example.com"));
    var user = await AddCustomerAccountAsync("Carla");
    var claim = await _claims.GetByIdAsync((await SubmitClaimAsync(user)).Id);

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _claims.ApproveAsync(_admin.Id, claim.Id, new CustomerClaimApproveRequest(claim.Version, historical.Id, false, null)));

    Assert.Contains("identityVerified", exception.Errors.Keys);
    Assert.Equal("Pending", (await _claims.GetByIdAsync(claim.Id)).Status);
    Assert.Null((await _dbContext.Customers.SingleAsync()).UserId);
  }

  [Fact]
  public async Task Approve_RefusesProfilesAlreadyClaimed_MergedProfiles_InactiveAccounts_AndStaleVersions()
  {
    var linked = await _customers.CreateAsync(_admin.Id, new CustomerCreateRequest("Ya Vinculado", "3001111111", "ya@example.com"));
    var owner = await AddCustomerAccountAsync("Dueño");
    await _customers.LinkAccountAsync(_admin.Id, linked.Id, new CustomerLinkAccountRequest(owner.Id, true));
    var merged = await _customers.CreateAsync(_admin.Id, new CustomerCreateRequest("Fusionado", "3002222222", "fusion@example.com"));
    var survivor = await _customers.CreateAsync(_admin.Id, new CustomerCreateRequest("Superviviente", "3003333333", "super@example.com"));
    await _customers.MergeWithPreviewAsync(_admin.Id, survivor.Id, merged.Id);

    var user = await AddCustomerAccountAsync("Reclamante");
    var claim = await _claims.GetByIdAsync((await SubmitClaimAsync(user)).Id);

    await Assert.ThrowsAsync<ConflictException>(() =>
        _claims.ApproveAsync(_admin.Id, claim.Id, new CustomerClaimApproveRequest(claim.Version, linked.Id, true, null)));
    await Assert.ThrowsAsync<ConflictException>(() =>
        _claims.ApproveAsync(_admin.Id, claim.Id, new CustomerClaimApproveRequest(claim.Version, merged.Id, true, null)));
    await Assert.ThrowsAsync<ConflictException>(() =>
        _claims.ApproveAsync(_admin.Id, claim.Id, new CustomerClaimApproveRequest(claim.Version + 1, survivor.Id, true, null)));

    var tracked = await _dbContext.Users.SingleAsync(candidate => candidate.Id == user.Id);
    tracked.Deactivate(DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();
    await Assert.ThrowsAsync<ConflictException>(() =>
        _claims.ApproveAsync(_admin.Id, claim.Id, new CustomerClaimApproveRequest(claim.Version, survivor.Id, true, null)));

    Assert.Equal("Pending", (await _claims.GetByIdAsync(claim.Id)).Status);
    Assert.Equal(owner.Id, (await _dbContext.Customers.SingleAsync(customer => customer.Id == linked.Id)).UserId);
  }

  [Fact]
  public async Task Reject_RequiresAReason_IsFinal_AndTheCustomerSeesTheReason_ThenCanClaimAgain()
  {
    var user = await AddCustomerAccountAsync("Carla");
    var claim = await _claims.GetByIdAsync((await SubmitClaimAsync(user)).Id);

    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _claims.RejectAsync(_admin.Id, claim.Id, new CustomerClaimRejectRequest(claim.Version, " ")));
    var rejected = await _claims.RejectAsync(_admin.Id, claim.Id, new CustomerClaimRejectRequest(claim.Version, "No presentó documento"));

    Assert.Equal("Rejected", rejected.Status);
    await Assert.ThrowsAsync<ConflictException>(() =>
        _claims.ApproveAsync(_admin.Id, claim.Id, new CustomerClaimApproveRequest(claim.Version, Guid.NewGuid(), true, null)));
    var status = await _claims.GetStatusAsync(user.Id);
    Assert.Null(status.PendingClaim);
    Assert.Equal("No presentó documento", Assert.Single(status.Claims).RejectionReason);

    var again = await SubmitClaimAsync(user);
    Assert.Equal("Pending", again.Status);
  }

  [Fact]
  public async Task Candidates_SuggestByTheClaimsPhoneOrEmail_WithHintsOnly_AndMarkLinkedProfiles()
  {
    var byPhone = await _customers.CreateAsync(_admin.Id, new CustomerCreateRequest("Carla P.", "+57 300 123 4567", "otro@example.com"));
    var byEmail = await _customers.CreateAsync(_admin.Id, new CustomerCreateRequest("C. Pérez", "3119998888", "carla@example.com"));
    await _customers.CreateAsync(_admin.Id, new CustomerCreateRequest("Nadie", "3005556666", "nadie@example.com"));
    var user = await AddCustomerAccountAsync("Carla");
    var claim = await SubmitClaimAsync(user, phone: "300 123 4567", email: "carla@example.com");

    var suggested = await _claims.GetCandidatesAsync(claim.Id, null);

    Assert.Equal([byEmail.Id, byPhone.Id], suggested.Select(candidate => candidate.CustomerId));
    var phoneMatch = suggested.Single(candidate => candidate.CustomerId == byPhone.Id);
    Assert.Equal(["phone"], phoneMatch.MatchedOn);
    Assert.Equal(("•••4567", "o•••@example.com", false), (phoneMatch.PhoneHint, phoneMatch.EmailHint, phoneMatch.IsLinked));

    var searched = await _claims.GetCandidatesAsync(claim.Id, "Nadie");
    Assert.Equal("Nadie", Assert.Single(searched).FullName);
  }

  // ── Relations ──────────────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task AServiceRecordFromTheAccountsAppointment_SuggestsTheProfileLinkedLater_WithoutAssigningIt()
  {
    var user = await AddCustomerAccountAsync("Carla");
    var barberUser = new User("Barbero", "barbero@example.com", "hash", DateTime.UtcNow);
    var barber = new StaffProfile(barberUser.Id, "Alex", 30, DateTime.UtcNow);
    var startsAt = DateTime.UtcNow.AddHours(-2);
    var appointment = new Appointment(barber.Id, "Carla", startsAt, startsAt.AddMinutes(30), AppointmentStatus.Completed,
        AppointmentSource.CustomerBooking, DateTime.UtcNow, user.Id);
    _dbContext.Users.Add(barberUser);
    _dbContext.StaffProfiles.Add(barber);
    _dbContext.Appointments.Add(appointment);
    await _dbContext.SaveChangesAsync();
    var records = new ServiceRecordsService(_dbContext, new MembershipLedgerService(_dbContext, _memberships, TimeProvider.System), TimeProvider.System);
    var record = Assert.Single(await records.RegisterAsync(barberUser.Id,
        new ServiceRecordRegisterRequest(appointment.Id, null, null, [_haircut.Id], null, Guid.NewGuid())));
    Assert.Null(record.CustomerId);
    Assert.Null(record.SuggestedCustomerId);

    var profile = await _claims.CreateOwnProfileAsync(user.Id, new OwnCustomerProfileCreateRequest("Carla", "3001234567", user.Email));

    var reviewed = await records.GetByIdAsync(record.Id);
    Assert.Null(reviewed.CustomerId);
    Assert.Equal((profile.Id, "Carla"), (reviewed.SuggestedCustomerId!.Value, reviewed.SuggestedCustomerName!));
  }

  [Fact]
  public async Task MergingAClaimedProfile_KeepsMemberships_ClaimHistory_AndServiceRecords_WithoutDeletingAnything()
  {
    var (historical, membership) = await HistoricalCustomerWithMembershipAsync();
    var user = await AddCustomerAccountAsync("Carla");
    var claim = await _claims.GetByIdAsync((await SubmitClaimAsync(user)).Id);
    await _claims.ApproveAsync(_admin.Id, claim.Id, new CustomerClaimApproveRequest(claim.Version, historical.Id, true, null));
    var survivor = await _customers.CreateAsync(_admin.Id, new CustomerCreateRequest("Carla Pérez", "3009990000", "carla.p@example.com"));
    var customersBefore = await _dbContext.Customers.CountAsync();

    await _customers.MergeWithPreviewAsync(_admin.Id, survivor.Id, historical.Id);

    // Nothing deleted; the membership follows the survivor and keeps who bought it; the account moves with it.
    Assert.Equal(customersBefore, await _dbContext.Customers.CountAsync());
    var moved = await _dbContext.CustomerMemberships.SingleAsync(item => item.Id == membership.Id);
    Assert.Equal((survivor.Id, historical.Id), (moved.CustomerId, moved.OriginalCustomerId));
    Assert.Equal(user.Id, (await _dbContext.Customers.SingleAsync(customer => customer.Id == survivor.Id)).UserId);
    // The claim keeps the profile it was approved for, which now points to the survivor.
    Assert.Equal(historical.Id, (await _claims.GetByIdAsync(claim.Id)).ApprovedCustomerId);
    Assert.Equal(survivor.Id, (await _dbContext.Customers.SingleAsync(customer => customer.Id == historical.Id)).MergedIntoCustomerId);
    Assert.Equal(survivor.Id, (await _claims.GetStatusAsync(user.Id)).Profile!.Id);
  }

  // ── Helpers ────────────────────────────────────────────────────────────────────────────────

  private async Task<User> AddCustomerAccountAsync(string name, string? email = null)
  {
    var user = new User(name, email ?? $"{Guid.NewGuid():N}@example.com", "hash", DateTime.UtcNow);
    user.UserRoles.Add(new UserRole(user.Id, _customerRole.Id, DateTime.UtcNow));
    _dbContext.Users.Add(user);
    await _dbContext.SaveChangesAsync();
    return user;
  }

  private Task<OwnCustomerClaimView> SubmitClaimAsync(User user, Guid? key = null, string phone = "3001234567", string? email = null)
      => _claims.SubmitClaimAsync(user.Id, new CustomerClaimCreateRequest(user.FullName, phone, email ?? user.Email, "Cliente desde 2024", key ?? Guid.NewGuid()));

  private async Task<(CustomerDetailView Customer, CustomerMembershipDetailView Membership)> HistoricalCustomerWithMembershipAsync()
  {
    var plan = await _plans.CreateAsync(new MembershipPlanUpsertRequest("Oro", null, 250000, "Pool", 10, 90, Terms, null, [new(_haircut.Id, null)], []));
    var customer = await _customers.CreateAsync(_admin.Id, new CustomerCreateRequest("Carla Histórica", "3001234567", "historica@example.com"));
    var membership = await _memberships.SellAsync(_admin.Id, customer.Id, new MembershipSaleRequest(plan.Id, _today, ActivateNow: true));
    return (customer, membership);
  }
}
