using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Loyalty.Redemptions;
using Barbershop.Application.Loyalty.Wallet;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Application.ServiceRecords;
using Barbershop.Domain.Common;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.ServiceRecords;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Customers;
using Barbershop.Infrastructure.Finance;
using Barbershop.Infrastructure.Loyalty;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.ServiceRecords;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Barbershop.Tests.Features.Loyalty;

/// <summary>
/// Rewards and redemptions (ADR-0015) on the in-memory provider: reservation per lot (soonest expiry first), delivery
/// consuming it, single releases (expiring what is past due), deadlines, catalog snapshots, debt, program switch,
/// counter requests, service record deliveries with the commission snapshot, merges and reports. Every test that
/// moves points ends with the reconciliation of free, reserved, consumed and expired points against the movements.
/// Locks, unique indexes and real concurrency are in LoyaltyRedemptionPostgresTests.
/// </summary>
public sealed class LoyaltyRedemptionServiceTests : IDisposable
{
  private static readonly LoyaltyMovementType[] Openings =
      [LoyaltyMovementType.Accrual, LoyaltyMovementType.MergeTransferIn, LoyaltyMovementType.AdjustmentCredit];

  // 2026-11-04 12:00 in Bogotá.
  private readonly MutableClock _clock = new(new DateTimeOffset(2026, 11, 4, 17, 0, 0, TimeSpan.Zero));
  private readonly AppDbContext _dbContext;
  private readonly LoyaltyRedemptionService _redemptions;
  private readonly LoyaltyWalletService _wallet;
  private readonly CustomerManagementService _customers;
  private readonly User _admin;
  private readonly User _customerUser;
  private readonly Service _haircut;
  private readonly Service _beard;
  private readonly StaffProfile _barber;

  public LoyaltyRedemptionServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options;
    _dbContext = new AppDbContext(options);
    _dbContext.Database.EnsureCreated();
    _redemptions = new LoyaltyRedemptionService(_dbContext, _clock);
    _wallet = new LoyaltyWalletService(_dbContext, _clock);
    _customers = new CustomerManagementService(_dbContext, _clock);

    _admin = new User("Admin Principal", "admin@example.com", "hash", DateTime.UtcNow);
    _customerUser = new User("Carla Cliente", "carla@example.com", "hash", DateTime.UtcNow);
    var barberUser = new User("Alex Barbero", "alex@example.com", "hash", DateTime.UtcNow);
    _haircut = new Service("Corte", 25001, DateTime.UtcNow, 33);
    _beard = new Service("Barba", 15000, DateTime.UtcNow, 40);
    _barber = new StaffProfile(barberUser.Id, "Alex", 30, DateTime.UtcNow);
    _dbContext.Users.AddRange(_admin, _customerUser, barberUser);
    _dbContext.Services.AddRange(_haircut, _beard);
    _dbContext.StaffProfiles.Add(_barber);
    _dbContext.SaveChanges();
    SetProgram(enabled: true);
  }

  private DateOnly Today => DateOnly.FromDateTime(BogotaClock.ToLocal(_clock.GetUtcNow().UtcDateTime));

  public void Dispose() => _dbContext.Dispose();

  // ── Reservation, delivery and release ──────────────────────────────────────────────────────

  [Fact]
  public async Task ARequestReservesSoonestExpiryFirst_PerLot_AndMovesPointsFromAvailableToReserved()
  {
    var customerId = await CreateLinkedCustomerAsync();
    var later = await SeedLotAsync(customerId, 30, validityDays: 300);
    var sooner = await SeedLotAsync(customerId, 20, validityDays: 10);
    var reward = await CreateRewardAsync("Gorra", "Other", null, cost: 35);

    var requested = await RequestAsync(reward.Id);

    Assert.Equal("Requested", requested.Status);
    var allocations = await _dbContext.LoyaltyRedemptionAllocations.AsNoTracking().ToListAsync();
    Assert.Equal(20, allocations.Single(allocation => allocation.LotId == sooner).Points);
    Assert.Equal(15, allocations.Single(allocation => allocation.LotId == later).Points);
    var wallet = await _wallet.GetWalletAsync(customerId);
    Assert.Equal((15, 35), (wallet.AvailablePoints, wallet.ReservedPoints));
    Assert.Equal(15, (await _redemptions.GetCatalogAsync(_customerUser.Id)).AvailablePoints);
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task DeliveringConsumesTheReservation_WithoutDebitingTheAvailablePointsAgain()
  {
    var customerId = await CreateLinkedCustomerAsync();
    await SeedLotAsync(customerId, 50);
    var reward = await CreateRewardAsync("Gorra", "Other", null, cost: 30);
    var requested = await RequestAsync(reward.Id);
    var approved = await _redemptions.ApproveAsync(_admin.Id, requested.Id, new LoyaltyRedemptionDecisionRequest((await _redemptions.GetAsync(requested.Id)).Version));

    var delivered = await _redemptions.DeliverAsync(_admin.Id, requested.Id, new LoyaltyRedemptionDecisionRequest(approved.Version));

    Assert.Equal(("Delivered", "Admin Principal", "Admin Principal"), (delivered.Status, delivered.ApprovedByName, delivered.DeliveredByName));
    var wallet = await _wallet.GetWalletAsync(customerId);
    Assert.Equal((20, 0), (wallet.AvailablePoints, wallet.ReservedPoints));
    var consumed = await _dbContext.LoyaltyMovements.Where(movement => movement.Type == LoyaltyMovementType.RedemptionConsumed).SumAsync(movement => movement.Points);
    Assert.Equal(30, consumed);
    // A repeated delivery returns the stored one and consumes nothing more.
    await _redemptions.DeliverAsync(_admin.Id, requested.Id, new LoyaltyRedemptionDecisionRequest(approved.Version));
    Assert.Equal(1, await _dbContext.LoyaltyMovements.CountAsync(movement => movement.Type == LoyaltyMovementType.RedemptionConsumed));
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task RejectingReleasesExactlyWhatWasReserved_Once()
  {
    var customerId = await CreateLinkedCustomerAsync();
    await SeedLotAsync(customerId, 50);
    var reward = await CreateRewardAsync("Gorra", "Other", null, cost: 30);
    var requested = await RequestAsync(reward.Id);
    var version = (await _redemptions.GetAsync(requested.Id)).Version;

    await _redemptions.RejectAsync(_admin.Id, requested.Id, new LoyaltyRedemptionReasonRequest(version, "Sin existencias"));
    await _redemptions.RejectAsync(_admin.Id, requested.Id, new LoyaltyRedemptionReasonRequest(version, "Sin existencias"));

    Assert.Equal(1, await _dbContext.LoyaltyMovements.CountAsync(movement => movement.Type == LoyaltyMovementType.RedemptionRelease));
    var wallet = await _wallet.GetWalletAsync(customerId);
    Assert.Equal((50, 0), (wallet.AvailablePoints, wallet.ReservedPoints));
    // A different decision on a closed redemption is a conflict, and it releases nothing more.
    await Assert.ThrowsAsync<ConflictException>(() =>
        _redemptions.CancelAsync(_admin.Id, requested.Id, new LoyaltyRedemptionReasonRequest(version, "Otro motivo")));
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task CancellingAfterTheRestOfTheLotExpired_ExpiresTheReleasedPointsAtOnce_WithoutReappearing()
  {
    // Required by the user: reserve part of a lot, expire the rest, then cancel.
    var customerId = await CreateLinkedCustomerAsync();
    var lotId = await SeedLotAsync(customerId, 50, validityDays: 2);
    var reward = await CreateRewardAsync("Gorra", "Other", null, cost: 30);
    var requested = await RequestAsync(reward.Id);

    _clock.Advance(TimeSpan.FromDays(3));
    Assert.Equal(1, await _wallet.ExpireDueLotsAsync(CancellationToken.None));
    var lot = await _dbContext.LoyaltyLots.AsNoTracking().SingleAsync(candidate => candidate.Id == lotId);
    Assert.Equal((0, 30), (lot.RemainingPoints, lot.ReservedPoints));

    await _redemptions.CancelMineAsync(_customerUser.Id, requested.Id, new MyLoyaltyRedemptionCancelRequest(null));

    lot = await _dbContext.LoyaltyLots.AsNoTracking().SingleAsync(candidate => candidate.Id == lotId);
    Assert.Equal((0, 0), (lot.RemainingPoints, lot.ReservedPoints));
    var wallet = await _wallet.GetWalletAsync(customerId);
    Assert.Equal((0, 0), (wallet.AvailablePoints, wallet.ReservedPoints));
    var expirations = await _dbContext.LoyaltyMovements.AsNoTracking()
        .Where(movement => movement.LotId == lotId && movement.Type == LoyaltyMovementType.Expiration)
        .OrderBy(movement => movement.RedemptionId)
        .Select(movement => new { movement.Points, movement.RedemptionId })
        .ToListAsync();
    Assert.Equal(2, expirations.Count);
    Assert.Contains(expirations, expiration => expiration.Points == 20 && expiration.RedemptionId == null);
    Assert.Contains(expirations, expiration => expiration.Points == 30 && expiration.RedemptionId == requested.Id);
    // The job finds nothing more to expire.
    Assert.Equal(0, await _wallet.ExpireDueLotsAsync(CancellationToken.None));
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task ReleasingIntoAnExpiredLotBeforeTheJobRan_ExpiresOnlyTheReleasedPoints_TheJobExpiresTheRest()
  {
    var customerId = await CreateLinkedCustomerAsync();
    var lotId = await SeedLotAsync(customerId, 50, validityDays: 1);
    var reward = await CreateRewardAsync("Gorra", "Other", null, cost: 30);
    var requested = await RequestAsync(reward.Id);
    _clock.Advance(TimeSpan.FromDays(1));

    await _redemptions.CancelMineAsync(_customerUser.Id, requested.Id, new MyLoyaltyRedemptionCancelRequest("Cambié de idea"));
    Assert.Equal(1, await _wallet.ExpireDueLotsAsync(CancellationToken.None));

    var lot = await _dbContext.LoyaltyLots.AsNoTracking().SingleAsync(candidate => candidate.Id == lotId);
    Assert.Equal((0, 0), (lot.RemainingPoints, lot.ReservedPoints));
    Assert.Equal(0, (await _wallet.GetWalletAsync(customerId)).AvailablePoints);
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task DeliveryIsAllowedWhenTheReservedLotsExpiredMeanwhile()
  {
    var customerId = await CreateLinkedCustomerAsync();
    await SeedLotAsync(customerId, 30, validityDays: 1);
    var reward = await CreateRewardAsync("Gorra", "Other", null, cost: 30);
    var requested = await RequestAsync(reward.Id);
    var approved = await _redemptions.ApproveAsync(_admin.Id, requested.Id, new LoyaltyRedemptionDecisionRequest((await _redemptions.GetAsync(requested.Id)).Version));
    _clock.Advance(TimeSpan.FromDays(5));

    var delivered = await _redemptions.DeliverAsync(_admin.Id, requested.Id, new LoyaltyRedemptionDecisionRequest(approved.Version));

    Assert.Equal("Delivered", delivered.Status);
    Assert.Equal(0, (await _wallet.GetWalletAsync(customerId)).ReservedPoints);
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task PointsOfAMembershipAnnulledWhileReserved_AreWithdrawnWhenReleased_NotMadeUsable()
  {
    var customerId = await CreateLinkedCustomerAsync();
    var membershipLot = await SeedMembershipLotAsync(customerId, 40);
    var reward = await CreateRewardAsync("Gorra", "Other", null, cost: 40);
    var requested = await RequestAsync(reward.Id);
    var memberships = new MembershipSalesService(_dbContext, _clock);
    await memberships.AnnulAsync(_admin.Id, membershipLot.MembershipId, new MembershipAnnulRequest("Venta registrada por error"));

    await _redemptions.CancelMineAsync(_customerUser.Id, requested.Id, new MyLoyaltyRedemptionCancelRequest(null));

    Assert.Equal(0, (await _wallet.GetWalletAsync(customerId)).AvailablePoints);
    var withdrawal = await _dbContext.LoyaltyMovements.AsNoTracking()
        .SingleAsync(movement => movement.Type == LoyaltyMovementType.MembershipAnnulmentWithdrawal);
    Assert.Equal((40, requested.Id), (withdrawal.Points, withdrawal.RedemptionId!.Value));
    await AssertReconciledAsync();
  }

  // ── Debt and program switch ────────────────────────────────────────────────────────────────

  [Fact]
  public async Task WithDebtTheAvailablePointsAreUsableMinusDebt_AndAReleaseSettlesItWithTheCommonRule()
  {
    var customerId = await CreateLinkedCustomerAsync();
    await SeedLotAsync(customerId, 50);
    var reward = await CreateRewardAsync("Gorra", "Other", null, cost: 30);
    var requested = await RequestAsync(reward.Id);
    // A debt that a future reversal would open (none is created by this stage) appears while the points are reserved.
    var debtId = await SeedDebtAsync(customerId, 25);

    // 20 usable − 25 owed: no new request fits, and the reserved points do not settle the debt.
    var catalog = await _redemptions.GetCatalogAsync(_customerUser.Id);
    Assert.Equal(0, catalog.AvailablePoints);
    var cheap = await CreateRewardAsync("Llavero", "Other", null, cost: 1);
    await Assert.ThrowsAsync<ConflictException>(() => RequestAsync(cheap.Id));
    Assert.Equal(25, (await _dbContext.LoyaltyDebts.AsNoTracking().SingleAsync(debt => debt.Id == debtId)).OutstandingPoints);

    await _redemptions.CancelMineAsync(_customerUser.Id, requested.Id, new MyLoyaltyRedemptionCancelRequest(null));

    Assert.Equal(0, (await _dbContext.LoyaltyDebts.AsNoTracking().SingleAsync(debt => debt.Id == debtId)).OutstandingPoints);
    Assert.Equal(25, (await _wallet.GetWalletAsync(customerId)).AvailablePoints);
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task WithTheProgramDisabled_NoNewRequests_ButPendingOnesGoOn()
  {
    var customerId = await CreateLinkedCustomerAsync();
    await SeedLotAsync(customerId, 50);
    var reward = await CreateRewardAsync("Gorra", "Other", null, cost: 30);
    var requested = await RequestAsync(reward.Id);
    SetProgram(enabled: false);

    var refused = await Assert.ThrowsAsync<ConflictException>(() => RequestAsync(reward.Id));
    Assert.Contains("deshabilitado", refused.Message);
    var approved = await _redemptions.ApproveAsync(_admin.Id, requested.Id, new LoyaltyRedemptionDecisionRequest((await _redemptions.GetAsync(requested.Id)).Version));
    var delivered = await _redemptions.DeliverAsync(_admin.Id, requested.Id, new LoyaltyRedemptionDecisionRequest(approved.Version));
    Assert.Equal("Delivered", delivered.Status);
    Assert.False((await _redemptions.GetCatalogAsync(_customerUser.Id)).Rewards.Single().CanAfford);
  }

  // ── Catalog snapshots ──────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task EditingOrDeactivatingTheReward_NeverChangesExistingRequests_AndIsAudited()
  {
    var customerId = await CreateLinkedCustomerAsync();
    await SeedLotAsync(customerId, 100);
    var reward = await CreateRewardAsync("Corte gratis", "Service", _haircut.Id, cost: 40);
    var requested = await RequestAsync(reward.Id);

    var edited = await _redemptions.UpdateAsync(
        _admin.Id, reward.Id, new LoyaltyRewardUpsertRequest("Barba gratis", null, "Service", _beard.Id, 60, IsActive: false, reward.Version));

    var stored = await _redemptions.GetAsync(requested.Id);
    Assert.Equal(("Corte gratis", "Service", _haircut.Id, "Corte", 40), (stored.RewardName, stored.RewardKind, stored.ServiceId!.Value, stored.ServiceName, stored.PointsCost));
    Assert.Equal(("Barba gratis", 60, false), (edited.Name, edited.PointsCost, edited.IsActive));
    await Assert.ThrowsAsync<ConflictException>(() => RequestAsync(reward.Id));
    var changes = await _redemptions.GetChangesAsync(reward.Id);
    Assert.Equal(["Updated", "Created"], changes.Select(change => change.ChangeType));
    Assert.Equal((40, 60, "Corte", "Barba"), (changes[0].PreviousPointsCost!.Value, changes[0].NewPointsCost, changes[0].PreviousServiceName, changes[0].NewServiceName));
    // A stale version (xmin) is refused: covered on PostgreSQL in LoyaltyRedemptionPostgresTests.
  }

  [Fact]
  public async Task TheRewardShapeIsValidated()
  {
    var missingService = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _redemptions.CreateAsync(_admin.Id, new LoyaltyRewardUpsertRequest("Corte", null, "Service", null, 10)));
    Assert.Contains("serviceId", missingService.Errors.Keys);
    var otherWithService = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _redemptions.CreateAsync(_admin.Id, new LoyaltyRewardUpsertRequest("Gorra", null, "Other", _haircut.Id, 10)));
    Assert.Contains("serviceId", otherWithService.Errors.Keys);
    var cost = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _redemptions.CreateAsync(_admin.Id, new LoyaltyRewardUpsertRequest("Gorra", null, "Other", null, 0)));
    Assert.Contains("pointsCost", cost.Errors.Keys);
  }

  // ── Deadlines (R7) ─────────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task DeadlinesAreInclusiveInBogota_RevalidatedOnApproveAndDeliver_AndCopiedIntoEachRequest()
  {
    var customerId = await CreateLinkedCustomerAsync();
    await SeedLotAsync(customerId, 100, validityDays: 365);
    var reward = await CreateRewardAsync("Gorra", "Other", null, cost: 10);
    var first = await RequestAsync(reward.Id);
    var stored = await _redemptions.GetAsync(first.Id);
    Assert.Equal((30, 30, Today, Today.AddDays(29)), (stored.ApprovalDays, stored.DeliveryDays, stored.RequestedOn, stored.ApproveBy));

    // Changing the settings only affects new requests.
    await new LoyaltySettingsService(_dbContext, _clock).UpdateAsync(
        _admin.Id, new Barbershop.Application.Loyalty.Admin.LoyaltySettingsUpdateRequest(true, 365, (await _dbContext.LoyaltySettings.AsNoTracking().SingleAsync()).Version, 5, 7));
    var second = await RequestAsync(reward.Id);
    Assert.Equal((30, 30), ((await _redemptions.GetAsync(first.Id)).ApprovalDays, (await _redemptions.GetAsync(first.Id)).DeliveryDays));
    Assert.Equal((5, 7, Today.AddDays(4)), ((await _redemptions.GetAsync(second.Id)).ApprovalDays, (await _redemptions.GetAsync(second.Id)).DeliveryDays, (await _redemptions.GetAsync(second.Id)).ApproveBy));

    // Last valid day of the second request: 23:59:59 in Bogotá of request day + 4.
    _clock.Set(new DateTimeOffset(2026, 11, 9, 4, 59, 59, TimeSpan.Zero));
    var approved = await _redemptions.ApproveAsync(_admin.Id, second.Id, new LoyaltyRedemptionDecisionRequest((await _redemptions.GetAsync(second.Id)).Version));
    Assert.Equal((new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 14)), (approved.ApprovedAtBogota(), approved.DeliverBy!.Value));

    // One second later the first request is still in time (30 days) ...
    _clock.Set(new DateTimeOffset(2026, 12, 4, 4, 59, 59, TimeSpan.Zero));
    Assert.False((await _redemptions.GetAsync(first.Id)).IsPastDeadline);
    // ... until its last day ends; then approving is refused even though the job has not run.
    _clock.Set(new DateTimeOffset(2026, 12, 4, 5, 0, 0, TimeSpan.Zero));
    var late = await _redemptions.GetAsync(first.Id);
    Assert.True(late.IsPastDeadline);
    await Assert.ThrowsAsync<ConflictException>(() => _redemptions.ApproveAsync(_admin.Id, first.Id, new LoyaltyRedemptionDecisionRequest(late.Version)));
    // The delivery deadline of the approved one (2026-11-14) passed too.
    await Assert.ThrowsAsync<ConflictException>(() =>
        _redemptions.DeliverAsync(_admin.Id, second.Id, new LoyaltyRedemptionDecisionRequest(approved.Version)));

    // The job closes both and releases their points, once.
    Assert.Equal(2, await _redemptions.ExpireDueRedemptionsAsync(CancellationToken.None));
    Assert.Equal(0, await _redemptions.ExpireDueRedemptionsAsync(CancellationToken.None));
    Assert.All(await _redemptions.ListAsync(null, customerId), redemption => Assert.Equal("Expired", redemption.Status));
    var wallet = await _wallet.GetWalletAsync(customerId);
    Assert.Equal((100, 0), (wallet.AvailablePoints, wallet.ReservedPoints));
    await AssertReconciledAsync();
  }

  // ── Transitions, idempotency and cancellation ──────────────────────────────────────────────

  [Fact]
  public async Task TransitionsFollowTheTable_AndRetriesReturnTheStoredResult()
  {
    var customerId = await CreateLinkedCustomerAsync();
    await SeedLotAsync(customerId, 100);
    var reward = await CreateRewardAsync("Gorra", "Other", null, cost: 10);
    var key = Guid.NewGuid();
    var requested = await _redemptions.RequestAsync(_customerUser.Id, new LoyaltyRedemptionRequest(reward.Id, key));
    var retried = await _redemptions.RequestAsync(_customerUser.Id, new LoyaltyRedemptionRequest(reward.Id, key));
    Assert.Equal(requested.Id, retried.Id);
    Assert.Equal(1, await _dbContext.LoyaltyRedemptions.CountAsync());
    var other = await CreateRewardAsync("Llavero", "Other", null, cost: 5);
    await Assert.ThrowsAsync<ConflictException>(() => _redemptions.RequestAsync(_customerUser.Id, new LoyaltyRedemptionRequest(other.Id, key)));

    var version = (await _redemptions.GetAsync(requested.Id)).Version;
    var approved = await _redemptions.ApproveAsync(_admin.Id, requested.Id, new LoyaltyRedemptionDecisionRequest(version));
    Assert.Equal("Approved", (await _redemptions.ApproveAsync(_admin.Id, requested.Id, new LoyaltyRedemptionDecisionRequest(version))).Status);
    await Assert.ThrowsAsync<ConflictException>(() =>
        _redemptions.RejectAsync(_admin.Id, requested.Id, new LoyaltyRedemptionReasonRequest(approved.Version, "Ya no")));

    // The customer may still cancel an approved one; nothing can follow a final state.
    var cancelled = await _redemptions.CancelMineAsync(_customerUser.Id, requested.Id, new MyLoyaltyRedemptionCancelRequest(null));
    Assert.Equal(("Cancelled", true, false), (cancelled.Status, cancelled.CancelledByCustomer, cancelled.CanCancel));
    await Assert.ThrowsAsync<ConflictException>(() =>
        _redemptions.DeliverAsync(_admin.Id, requested.Id, new LoyaltyRedemptionDecisionRequest(approved.Version)));
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task AServiceRewardCannotBeDeliveredDirectly_AndCustomersOnlySeeTheirOwn()
  {
    var customerId = await CreateLinkedCustomerAsync();
    await SeedLotAsync(customerId, 100);
    var reward = await CreateRewardAsync("Corte gratis", "Service", _haircut.Id, cost: 40);
    var requested = await RequestAsync(reward.Id);
    var approved = await _redemptions.ApproveAsync(_admin.Id, requested.Id, new LoyaltyRedemptionDecisionRequest((await _redemptions.GetAsync(requested.Id)).Version));

    var direct = await Assert.ThrowsAsync<ConflictException>(() =>
        _redemptions.DeliverAsync(_admin.Id, requested.Id, new LoyaltyRedemptionDecisionRequest(approved.Version)));
    Assert.Contains("servicio realizado", direct.Message);

    var stranger = new User("Otra Persona", "otra@example.com", "hash", DateTime.UtcNow);
    _dbContext.Users.Add(stranger);
    await _dbContext.SaveChangesAsync();
    await Assert.ThrowsAsync<NotFoundException>(() =>
        _redemptions.CancelMineAsync(stranger.Id, requested.Id, new MyLoyaltyRedemptionCancelRequest(null)));
    Assert.Empty(await _redemptions.ListMineAsync(stranger.Id));
    Assert.Single(await _redemptions.ListMineAsync(_customerUser.Id));
  }

  [Fact]
  public async Task ACounterRequestNeedsInPersonVerification_AndCanBeApprovedInTheSameStep()
  {
    var customer = await _customers.CreateAsync(_admin.Id, new CustomerCreateRequest("Pedro Mostrador", "3009876543", "pedro@example.com"));
    await SeedLotAsync(customer.Id, 30);
    var reward = await CreateRewardAsync("Gorra", "Other", null, cost: 30);

    var unverified = await Assert.ThrowsAsync<ValidationProblemException>(() => _redemptions.CreateAtCounterAsync(
        _admin.Id, customer.Id, new LoyaltyCounterRedemptionRequest(reward.Id, false, true, Guid.NewGuid())));
    Assert.Contains("inPersonVerified", unverified.Errors.Keys);

    var created = await _redemptions.CreateAtCounterAsync(
        _admin.Id, customer.Id, new LoyaltyCounterRedemptionRequest(reward.Id, true, true, Guid.NewGuid()));

    Assert.Equal(("Approved", "Counter", true), (created.Status, created.Channel, created.InPersonVerified));
    Assert.Equal((created.RequestedByName, created.ApprovedByName), ("Admin Principal", "Admin Principal"));
    await AssertReconciledAsync();
  }

  // ── Service records (R13, R14) ─────────────────────────────────────────────────────────────

  [Fact]
  public async Task ConfirmingARecordAsReward_DeliversTheRedemption_WithTheCommissionSnapshot_AndNoIncomeOrPoints()
  {
    var customerId = await CreateLinkedCustomerAsync();
    await SeedLotAsync(customerId, 100);
    var reward = await CreateRewardAsync("Corte gratis", "Service", _haircut.Id, cost: 40);
    var requested = await RequestAsync(reward.Id);
    await _redemptions.ApproveAsync(_admin.Id, requested.Id, new LoyaltyRedemptionDecisionRequest((await _redemptions.GetAsync(requested.Id)).Version));
    var records = CreateServiceRecordsService();
    var record = await SeedPendingRecordAsync(_haircut);

    var options = await records.GetRedemptionOptionsAsync(customerId, _haircut.Id);
    Assert.Equal(requested.Id, Assert.Single(options).Id);
    var confirmed = await records.ApproveAsync(_admin.Id, record.Id, RewardApproval(record, customerId, _haircut.Id, requested.Id));

    Assert.Equal(("LoyaltyReward", requested.Id, "Corte gratis"), (confirmed.Approval!.OperationType, confirmed.Approval.LoyaltyRedemptionId!.Value, confirmed.Approval.RewardName));
    // 25 001 at 33 %: the business share is truncated (8 250) and the professional gets the rest (16 751).
    Assert.Equal((25001, 33, 16751), (confirmed.Approval.RewardBasePriceSnapshot!.Value, confirmed.Approval.RewardBusinessPercentageSnapshot!.Value, confirmed.Approval.RewardProfessionalCommission!.Value));
    Assert.Equal(25001 - IncomeEntry.BusinessShare(25001, 33), confirmed.Approval.RewardProfessionalCommission.Value);
    var delivered = await _redemptions.GetAsync(requested.Id);
    Assert.Equal(("Delivered", record.Id), (delivered.Status, delivered.ServiceRecordId!.Value));
    Assert.Equal(0, await _dbContext.IncomeEntries.CountAsync());
    Assert.Equal(1, await _dbContext.LoyaltyLots.CountAsync());
    var wallet = await _wallet.GetWalletAsync(customerId);
    Assert.Equal((60, 0), (wallet.AvailablePoints, wallet.ReservedPoints));

    // A manual adjustment corrects points but never undoes the delivery, the record or its commission.
    await _wallet.AdjustAsync(_admin.Id, customerId, new LoyaltyAdjustmentRequest("Credit", 40, "Corrección del canje", Guid.NewGuid()));
    Assert.Equal("Delivered", (await _redemptions.GetAsync(requested.Id)).Status);
    var stored = await _dbContext.ServiceRecords.AsNoTracking().SingleAsync(candidate => candidate.Id == record.Id);
    Assert.Equal((ServiceRecordStatus.Approved, requested.Id, 16751), (stored.Status, stored.LoyaltyRedemptionId!.Value, stored.RewardProfessionalCommission!.Value));
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task ARewardRecordMustMatchTheRedemptionsCustomerAndService_AndDeliversItOnce()
  {
    var customerId = await CreateLinkedCustomerAsync();
    await SeedLotAsync(customerId, 100);
    var reward = await CreateRewardAsync("Corte gratis", "Service", _haircut.Id, cost: 40);
    var requested = await RequestAsync(reward.Id);
    await _redemptions.ApproveAsync(_admin.Id, requested.Id, new LoyaltyRedemptionDecisionRequest((await _redemptions.GetAsync(requested.Id)).Version));
    var records = CreateServiceRecordsService();
    var beardRecord = await SeedPendingRecordAsync(_beard);
    var otherCustomer = await _customers.CreateAsync(_admin.Id, new CustomerCreateRequest("Otro Cliente", "3005550000", "otro@example.com"));

    var wrongService = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        records.ApproveAsync(_admin.Id, beardRecord.Id, RewardApproval(beardRecord, customerId, _beard.Id, requested.Id)));
    Assert.Contains("loyaltyRedemptionId", wrongService.Errors.Keys);
    var wrongCustomer = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        records.ApproveAsync(_admin.Id, beardRecord.Id, RewardApproval(beardRecord, otherCustomer.Id, _haircut.Id, requested.Id)));
    Assert.Contains("loyaltyRedemptionId", wrongCustomer.Errors.Keys);
    var noRedemption = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        records.ApproveAsync(_admin.Id, beardRecord.Id, RewardApproval(beardRecord, customerId, _haircut.Id, null)));
    Assert.Contains("loyaltyRedemptionId", noRedemption.Errors.Keys);
    Assert.Equal("Approved", (await _redemptions.GetAsync(requested.Id)).Status);

    var first = await SeedPendingRecordAsync(_haircut);
    await records.ApproveAsync(_admin.Id, first.Id, RewardApproval(first, customerId, _haircut.Id, requested.Id));
    var second = await SeedPendingRecordAsync(_haircut);
    await Assert.ThrowsAsync<ConflictException>(() =>
        records.ApproveAsync(_admin.Id, second.Id, RewardApproval(second, customerId, _haircut.Id, requested.Id)));
    Assert.Equal(1, await _dbContext.LoyaltyMovements.CountAsync(movement => movement.Type == LoyaltyMovementType.RedemptionConsumed));
    await AssertReconciledAsync();
  }

  // ── Merges (D5) and reports (R14) ──────────────────────────────────────────────────────────

  [Fact]
  public async Task APendingRedemptionInEitherProfileBlocksTheMerge_AFinalOneDoesNot()
  {
    var customerId = await CreateLinkedCustomerAsync();
    await SeedLotAsync(customerId, 100);
    var duplicate = await _customers.CreateAsync(_admin.Id, new CustomerCreateRequest("Carla Duplicada", "3001112222", "dup@example.com"));
    var reward = await CreateRewardAsync("Gorra", "Other", null, cost: 10);
    var requested = await RequestAsync(reward.Id);

    var asTarget = await _customers.GetMergePreviewAsync(customerId, duplicate.Id);
    var asSource = await _customers.GetMergePreviewAsync(duplicate.Id, customerId);
    Assert.False(asTarget.CanMerge);
    Assert.Contains(asTarget.BlockingReasons, reason => reason.Contains("destino") && reason.Contains("canje"));
    Assert.Contains(asSource.BlockingReasons, reason => reason.Contains("origen") && reason.Contains("canje"));
    await Assert.ThrowsAsync<ConflictException>(() => _customers.MergeAsync(
        _admin.Id, customerId, new CustomerMergeRequest(duplicate.Id, asTarget.PreviewToken)));

    await _redemptions.CancelMineAsync(_customerUser.Id, requested.Id, new MyLoyaltyRedemptionCancelRequest(null));
    Assert.True((await _customers.GetMergePreviewAsync(customerId, duplicate.Id)).CanMerge);
  }

  [Fact]
  public async Task TheReportShowsRewardCommissionsApart_WithoutChangingTheFinancialTotals()
  {
    var customerId = await CreateLinkedCustomerAsync();
    await SeedLotAsync(customerId, 100);
    var reports = new ReportsQueryService(_dbContext, _clock);
    var before = await reports.GetSummaryAsync(Today, Today);
    var reward = await CreateRewardAsync("Corte gratis", "Service", _haircut.Id, cost: 40);
    var requested = await RequestAsync(reward.Id);
    await _redemptions.ApproveAsync(_admin.Id, requested.Id, new LoyaltyRedemptionDecisionRequest((await _redemptions.GetAsync(requested.Id)).Version));
    var record = await SeedPendingRecordAsync(_haircut);
    await CreateServiceRecordsService().ApproveAsync(_admin.Id, record.Id, RewardApproval(record, customerId, _haircut.Id, requested.Id));

    var after = await reports.GetSummaryAsync(Today, Today);

    Assert.Equal(
        (before.TotalIncome, before.TotalExpenses, before.NetProfit, before.IncomeCount, before.BusinessIncome, before.ProfessionalIncome),
        (after.TotalIncome, after.TotalExpenses, after.NetProfit, after.IncomeCount, after.BusinessIncome, after.ProfessionalIncome));
    var line = Assert.Single(after.RewardCommissions!);
    Assert.Equal((_barber.Id, "Alex", 1, 25001L, 8250L, 16751L), (line.StaffProfileId, line.StaffName, line.RewardCount, line.BasePriceTotal, line.BusinessShareTotal, line.ProfessionalCommissionTotal));
    Assert.Equal(16751, after.RewardCommissionsTotal);
  }

  // ── Helpers ────────────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Free, reserved, consumed and expired points of every lot against its movements: the opening is its only source,
  /// free = signed lot movements, reserved = holds − releases − consumptions; every allocation is held once and then
  /// released or consumed at most once, never both.
  /// </summary>
  private async Task AssertReconciledAsync()
  {
    var movements = await _dbContext.LoyaltyMovements.AsNoTracking().ToListAsync();
    foreach (var lot in await _dbContext.LoyaltyLots.AsNoTracking().ToListAsync())
    {
      var own = movements.Where(movement => movement.LotId == lot.Id).ToList();
      Assert.Equal(lot.InitialPoints, Assert.Single(own, movement => Openings.Contains(movement.Type)).Points);
      Assert.Equal(lot.RemainingPoints, own.Sum(movement => LoyaltyMovement.LotSign(movement.Type) * movement.Points));
      Assert.Equal(lot.ReservedPoints, own.Sum(movement => LoyaltyMovement.ReservedSign(movement.Type) * movement.Points));
      Assert.True(lot.RemainingPoints >= 0 && lot.ReservedPoints >= 0 && lot.RemainingPoints + lot.ReservedPoints <= lot.InitialPoints);
    }

    foreach (var allocation in await _dbContext.LoyaltyRedemptionAllocations.AsNoTracking().ToListAsync())
    {
      int Count(LoyaltyMovementType type) => movements.Count(movement =>
          movement.RedemptionId == allocation.RedemptionId && movement.LotId == allocation.LotId && movement.Type == type);
      int Points(LoyaltyMovementType type) => movements.Where(movement =>
          movement.RedemptionId == allocation.RedemptionId && movement.LotId == allocation.LotId && movement.Type == type).Sum(movement => movement.Points);
      Assert.Equal((1, allocation.Points), (Count(LoyaltyMovementType.RedemptionHold), Points(LoyaltyMovementType.RedemptionHold)));
      var closed = Count(LoyaltyMovementType.RedemptionRelease) + Count(LoyaltyMovementType.RedemptionConsumed);
      Assert.True(closed <= 1);
      var status = (await _dbContext.LoyaltyRedemptions.AsNoTracking().SingleAsync(redemption => redemption.Id == allocation.RedemptionId)).Status;
      Assert.Equal(LoyaltyRedemption.IsPendingStatus(status) ? 0 : 1, closed);
      Assert.Equal(status == LoyaltyRedemptionStatus.Delivered ? allocation.Points : 0, Points(LoyaltyMovementType.RedemptionConsumed));
    }

    foreach (var debt in await _dbContext.LoyaltyDebts.AsNoTracking().ToListAsync())
    {
      var own = movements.Where(movement => movement.DebtId == debt.Id).ToList();
      Assert.Equal(debt.OutstandingPoints, own.Sum(movement => LoyaltyMovement.DebtSign(movement.Type) * movement.Points));
    }
  }

  private async Task<Guid> CreateLinkedCustomerAsync()
  {
    var profile = new CustomerProfile("Carla Cliente", "3001234567", "carla@example.com", _admin.Id, DateTime.UtcNow);
    profile.LinkAccount(_customerUser.Id, _admin.Id, DateTime.UtcNow);
    _dbContext.Customers.Add(profile);
    await _dbContext.SaveChangesAsync();
    _dbContext.ChangeTracker.Clear();
    return profile.Id;
  }

  private async Task<Guid> SeedLotAsync(Guid customerId, int points, int validityDays = 365)
  {
    var now = _clock.GetUtcNow().UtcDateTime;
    var lot = LoyaltyLot.Accrue(customerId, LoyaltySourceType.Adjustment, Guid.NewGuid(), points, now, Today, validityDays);
    _dbContext.LoyaltyLots.Add(lot);
    _dbContext.LoyaltyMovements.Add(new LoyaltyMovement(customerId, LoyaltyMovementType.AdjustmentCredit, points, now, lotId: lot.Id, performedByUserId: _admin.Id));
    await _dbContext.SaveChangesAsync();
    _dbContext.ChangeTracker.Clear();
    return lot.Id;
  }

  private async Task<(Guid LotId, Guid MembershipId)> SeedMembershipLotAsync(Guid customerId, int points)
  {
    var plans = new MembershipPlanManagementService(_dbContext, _clock, new FixedCurrentUserAccessor(_admin.Id));
    var plan = await plans.CreateAsync(new MembershipPlanUpsertRequest(
        "Oro", null, 250000, "Pool", 10, 90, "La membresía es personal e intransferible.", null, [new(_haircut.Id, null)], [], points));
    var membership = await new MembershipSalesService(_dbContext, _clock).SellAsync(_admin.Id, customerId, new MembershipSaleRequest(plan.Id, Today, ActivateNow: true));
    var income = IncomeEntry.CreateMembershipSale(250000, Today, _admin.Id, DateTime.UtcNow);
    _dbContext.IncomeEntries.Add(income);
    await _dbContext.SaveChangesAsync();
    await new MembershipSalesService(_dbContext, _clock).LinkIncomeAsync(_admin.Id, membership.Id, new MembershipIncomeLinkRequest(income.Id));
    _dbContext.ChangeTracker.Clear();
    var lot = await _dbContext.LoyaltyLots.AsNoTracking().SingleAsync(candidate => candidate.SourceId == membership.Id);
    return (lot.Id, membership.Id);
  }

  private async Task<Guid> SeedDebtAsync(Guid customerId, int points)
  {
    var now = _clock.GetUtcNow().UtcDateTime;
    var debt = new LoyaltyDebt(customerId, LoyaltyDebtOrigin.PointsReversal, points, now);
    _dbContext.LoyaltyDebts.Add(debt);
    _dbContext.LoyaltyMovements.Add(new LoyaltyMovement(customerId, LoyaltyMovementType.DebtOpened, points, now, debtId: debt.Id, performedByUserId: _admin.Id));
    await _dbContext.SaveChangesAsync();
    _dbContext.ChangeTracker.Clear();
    return debt.Id;
  }

  private Task<LoyaltyRewardView> CreateRewardAsync(string name, string kind, Guid? serviceId, int cost)
      => _redemptions.CreateAsync(_admin.Id, new LoyaltyRewardUpsertRequest(name, null, kind, serviceId, cost));

  private Task<MyLoyaltyRedemptionView> RequestAsync(Guid rewardId)
      => _redemptions.RequestAsync(_customerUser.Id, new LoyaltyRedemptionRequest(rewardId, Guid.NewGuid()));

  private void SetProgram(bool enabled)
  {
    var settings = _dbContext.LoyaltySettings.Single();
    settings.Update(enabled, settings.PointsValidityDays, _admin.Id, DateTime.UtcNow);
    _dbContext.SaveChanges();
    _dbContext.ChangeTracker.Clear();
  }

  private ServiceRecordsService CreateServiceRecordsService()
  {
    var memberships = new MembershipSalesService(_dbContext, _clock);
    return new ServiceRecordsService(_dbContext, new MembershipLedgerService(_dbContext, memberships, _clock), _clock);
  }

  private async Task<ServiceRecord> SeedPendingRecordAsync(Service service)
  {
    var record = new ServiceRecord(
        Guid.NewGuid(), 0, null, _barber.Id, service.Id, service.Name, Today, null, "Carla", null, _admin.Id, _clock.GetUtcNow().UtcDateTime);
    _dbContext.ServiceRecords.Add(record);
    await _dbContext.SaveChangesAsync();
    _dbContext.ChangeTracker.Clear();
    return record;
  }

  private static ServiceRecordApproveRequest RewardApproval(ServiceRecord record, Guid customerId, Guid serviceId, Guid? redemptionId)
      => new(record.Version, "LoyaltyReward", serviceId, record.StaffProfileId, customerId, record.PerformedOn, null, null, false, null, redemptionId);

  private sealed class FixedCurrentUserAccessor(Guid userId) : Barbershop.Application.Auth.ICurrentUserAccessor
  {
    public Guid? UserId { get; } = userId;
  }

  private sealed class MutableClock(DateTimeOffset utcNow) : TimeProvider
  {
    private DateTimeOffset _utcNow = utcNow;

    public void Advance(TimeSpan by) => _utcNow += by;

    public void Set(DateTimeOffset utcNow) => _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;
  }
}

internal static class LoyaltyRedemptionViewTestExtensions
{
  public static DateOnly ApprovedAtBogota(this LoyaltyRedemptionView view)
      => DateOnly.FromDateTime(BogotaClock.ToLocal(view.ApprovedAt!.Value));
}
