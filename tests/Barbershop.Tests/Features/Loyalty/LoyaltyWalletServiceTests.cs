using Barbershop.Application.Auth;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Loyalty.Wallet;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Domain.Common;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.Memberships;
using Barbershop.Domain.Services;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Customers;
using Barbershop.Infrastructure.Loyalty;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Barbershop.Tests.Features.Loyalty;

/// <summary>
/// The points wallet (ADR-0013, ADR-0014): membership accrual at the first income link, annulment withdrawals,
/// merges with lots and debts, adjustments, expiration and chain resolution. Every test ends reconciling the stored
/// projections with the movements. Row locks and real concurrency are in LoyaltyWalletPostgresTests.
/// </summary>
public sealed class LoyaltyWalletServiceTests : IDisposable
{
  private const string Terms = "La membresía es personal e intransferible.";

  private readonly MutableClock _clock = new(new DateTimeOffset(2026, 11, 3, 17, 0, 0, TimeSpan.Zero));
  private readonly AppDbContext _dbContext;
  private readonly CustomerManagementService _customers;
  private readonly MembershipSalesService _memberships;
  private readonly MembershipPlanManagementService _plans;
  private readonly LoyaltyWalletService _wallet;
  private readonly User _admin;
  private readonly Service _haircut;

  public LoyaltyWalletServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options;
    _dbContext = new AppDbContext(options);
    _dbContext.Database.EnsureCreated();
    _customers = new CustomerManagementService(_dbContext, _clock);
    _memberships = new MembershipSalesService(_dbContext, _clock);
    _admin = new User("Admin Principal", "admin@example.com", "hash", DateTime.UtcNow);
    _plans = new MembershipPlanManagementService(_dbContext, _clock, new FixedCurrentUserAccessor(_admin.Id));
    _wallet = new LoyaltyWalletService(_dbContext, _clock);

    _haircut = new Service("Corte", 25000, DateTime.UtcNow, 30);
    _dbContext.Users.Add(_admin);
    _dbContext.Services.Add(_haircut);
    _dbContext.SaveChanges();
  }

  private DateOnly Today => DateOnly.FromDateTime(BogotaClock.ToLocal(_clock.GetUtcNow().UtcDateTime));

  public void Dispose() => _dbContext.Dispose();

  // ── Membership: first income link ──────────────────────────────────────────────────────────

  [Fact]
  public async Task TheSaleCopiesThePlanPoints_AndOnlyTheFirstIncomeLinkCreditsThem_UnlinkingKeepsThem()
  {
    await SetProgramAsync(enabled: true);
    var customer = await CreateCustomerAsync("Carla");
    var membership = await SellAsync(customer.Id, planPoints: 40);
    Assert.Equal((40, false, null), (membership.Loyalty!.PointsSnapshot, membership.Loyalty.PredatesWallet, membership.Loyalty.Decision));

    var linked = await _memberships.LinkIncomeAsync(_admin.Id, membership.Id, new MembershipIncomeLinkRequest(await AddIncomeAsync()));
    Assert.Equal(("Accrued", 40), (linked.Loyalty!.Decision, linked.Loyalty.AccruedPoints));

    // Unlinking keeps the points; linking again (even another income) credits nothing more.
    await _memberships.UnlinkIncomeAsync(_admin.Id, membership.Id, new MembershipIncomeUnlinkRequest());
    var relinked = await _memberships.LinkIncomeAsync(_admin.Id, membership.Id, new MembershipIncomeLinkRequest(await AddIncomeAsync()));

    Assert.Equal(("Accrued", 40), (relinked.Loyalty!.Decision, relinked.Loyalty.AccruedPoints));
    var lot = await _dbContext.LoyaltyLots.SingleAsync();
    Assert.Equal((customer.Id, LoyaltySourceType.Membership, membership.Id, 40), (lot.CustomerId, lot.SourceType, lot.SourceId, lot.RemainingPoints));
    Assert.Equal((Today, Today.AddDays(364)), (lot.AccruedOn, lot.ExpiresOn));
    Assert.Equal(40, (await _wallet.GetWalletAsync(customer.Id)).AvailablePoints);
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task AFirstLinkThatDoesNotAccrue_IsFinal_EvenAfterEnablingTheProgramAndLinkingAgain()
  {
    await SetProgramAsync(enabled: false);
    var customer = await CreateCustomerAsync("Carla");
    var disabled = await SellAsync(customer.Id, planPoints: 40);
    var noPoints = await SellAsync(customer.Id, planPoints: 0, planName: "Plata");

    var disabledLinked = await _memberships.LinkIncomeAsync(_admin.Id, disabled.Id, new MembershipIncomeLinkRequest(await AddIncomeAsync()));
    await SetProgramAsync(enabled: true);
    var noPointsLinked = await _memberships.LinkIncomeAsync(_admin.Id, noPoints.Id, new MembershipIncomeLinkRequest(await AddIncomeAsync()));
    Assert.Equal("NotAccruedProgramDisabled", disabledLinked.Loyalty!.Decision);
    Assert.Equal(("NotAccruedNoPoints", 0, false), (noPointsLinked.Loyalty!.Decision, noPointsLinked.Loyalty.PointsSnapshot!.Value, noPointsLinked.Loyalty.PredatesWallet));

    await _memberships.UnlinkIncomeAsync(_admin.Id, disabled.Id, new MembershipIncomeUnlinkRequest());
    var relinked = await _memberships.LinkIncomeAsync(_admin.Id, disabled.Id, new MembershipIncomeLinkRequest(await AddIncomeAsync()));

    Assert.Equal(("NotAccruedProgramDisabled", 0), (relinked.Loyalty!.Decision, relinked.Loyalty.AccruedPoints));
    Assert.Equal(0, await _dbContext.LoyaltyLots.CountAsync());
  }

  [Fact]
  public async Task AMembershipSoldBeforeTheWallet_IsDistinctFromAZeroPointsOne_AndNeverAccrues()
  {
    await SetProgramAsync(enabled: true);
    var customer = await CreateCustomerAsync("Carla");
    var legacy = await SellAsync(customer.Id, planPoints: 40);
    var stored = await _dbContext.CustomerMemberships.SingleAsync(candidate => candidate.Id == legacy.Id);
    _dbContext.Entry(stored).Property(candidate => candidate.LoyaltyPointsSnapshot).CurrentValue = null;
    await _dbContext.SaveChangesAsync();

    var before = await _memberships.GetByIdAsync(legacy.Id);
    Assert.Equal((null, true, null), (before.Loyalty!.PointsSnapshot, before.Loyalty.PredatesWallet, before.Loyalty.Decision));

    var linked = await _memberships.LinkIncomeAsync(_admin.Id, legacy.Id, new MembershipIncomeLinkRequest(await AddIncomeAsync()));
    Assert.Equal(("NotAccruedPredatesWallet", true), (linked.Loyalty!.Decision, linked.Loyalty.PredatesWallet));
    Assert.Equal(0, await _dbContext.LoyaltyLots.CountAsync());
  }

  [Fact]
  public async Task LinkingAMembershipAnnulledBeforeItsFirstLink_DecidesNotToAccrue()
  {
    await SetProgramAsync(enabled: true);
    var customer = await CreateCustomerAsync("Carla");
    var membership = await SellAsync(customer.Id, planPoints: 40);
    await _memberships.AnnulAsync(_admin.Id, membership.Id, new MembershipAnnulRequest("Venta registrada por error"));

    var linked = await _memberships.LinkIncomeAsync(_admin.Id, membership.Id, new MembershipIncomeLinkRequest(await AddIncomeAsync()));

    Assert.Equal("NotAccruedAnnulled", linked.Loyalty!.Decision);
    Assert.Equal(0, await _dbContext.LoyaltyLots.CountAsync());
  }

  // ── Annulment ──────────────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task Annulment_WithdrawsOnlyTheUsablePointsOfItsMembership_NeverExpiredOnes_OtherSources_OrNewDebt()
  {
    await SetProgramAsync(enabled: true);
    var customer = await CreateCustomerAsync("Carla");
    var membership = await SellAsync(customer.Id, planPoints: 40);
    await SeedDebtAsync(customer.Id, 15);
    await _memberships.LinkIncomeAsync(_admin.Id, membership.Id, new MembershipIncomeLinkRequest(await AddIncomeAsync()));
    await CreditAsync(customer.Id, 30);

    var annulled = await _memberships.AnnulAsync(_admin.Id, membership.Id, new MembershipAnnulRequest("Pago devuelto"));

    // 40 accrued − 15 that settled the debt = 25 withdrawn; the manual credit is untouched; no debt is created.
    Assert.Equal((40, 25), (annulled.Loyalty!.AccruedPoints, annulled.Loyalty.WithdrawnPoints));
    var wallet = await _wallet.GetWalletAsync(customer.Id);
    Assert.Equal((30, 0), (wallet.AvailablePoints, wallet.DebtPoints));
    Assert.Single(wallet.Debts);

    // A retried annulment is refused and withdraws nothing more.
    await Assert.ThrowsAsync<ConflictException>(() =>
        _memberships.AnnulAsync(_admin.Id, membership.Id, new MembershipAnnulRequest("Pago devuelto")));
    Assert.Equal(1, await _dbContext.LoyaltyMovements.CountAsync(movement => movement.Type == LoyaltyMovementType.MembershipAnnulmentWithdrawal));
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task AnnulmentOfAnExpiredMembershipLot_WithdrawsNothing_AndCreatesNoDebt()
  {
    await SetProgramAsync(enabled: true, validityDays: 2);
    var customer = await CreateCustomerAsync("Carla");
    var membership = await SellAsync(customer.Id, planPoints: 40);
    await _memberships.LinkIncomeAsync(_admin.Id, membership.Id, new MembershipIncomeLinkRequest(await AddIncomeAsync()));

    _clock.Advance(TimeSpan.FromDays(3));
    var annulled = await _memberships.AnnulAsync(_admin.Id, membership.Id, new MembershipAnnulRequest("Pago devuelto"));

    Assert.Equal(0, annulled.Loyalty!.WithdrawnPoints);
    Assert.Equal(40, (await _dbContext.LoyaltyLots.SingleAsync()).RemainingPoints);
    Assert.Equal(0, await _dbContext.LoyaltyDebts.CountAsync());
  }

  [Fact]
  public async Task AnnulmentAfterTwoMerges_WithdrawsFromTheLotThatDescendsInTheSurvivor_LeavingTheMovedLotsClosed()
  {
    await SetProgramAsync(enabled: true);
    var first = await CreateCustomerAsync("Carla Uno");
    var second = await CreateCustomerAsync("Carla Dos");
    var third = await CreateCustomerAsync("Carla Tres");
    var membership = await SellAsync(first.Id, planPoints: 40);
    await _memberships.LinkIncomeAsync(_admin.Id, membership.Id, new MembershipIncomeLinkRequest(await AddIncomeAsync()));
    await CreditAsync(third.Id, 10);

    await _customers.MergeWithPreviewAsync(_admin.Id, second.Id, first.Id);
    await _customers.MergeWithPreviewAsync(_admin.Id, third.Id, second.Id);
    Assert.Equal(50, (await _wallet.GetWalletAsync(third.Id)).AvailablePoints);

    var annulled = await _memberships.AnnulAsync(_admin.Id, membership.Id, new MembershipAnnulRequest("Pago devuelto"));

    Assert.Equal(40, annulled.Loyalty!.WithdrawnPoints);
    var membershipLots = await _dbContext.LoyaltyLots.Where(lot => lot.SourceId == membership.Id).OrderBy(lot => lot.CreatedAt).ToListAsync();
    Assert.Equal(3, membershipLots.Count);
    Assert.All(membershipLots, lot => Assert.Equal(0, lot.RemainingPoints));
    Assert.Equal((first.Id, second.Id, third.Id), (membershipLots[0].CustomerId, membershipLots[1].CustomerId, membershipLots[2].CustomerId));
    Assert.All(membershipLots.Skip(1), lot => Assert.Equal(first.Id, lot.OriginCustomerId));
    var withdrawal = await _dbContext.LoyaltyMovements.SingleAsync(movement => movement.Type == LoyaltyMovementType.MembershipAnnulmentWithdrawal);
    Assert.Equal(membershipLots[2].Id, withdrawal.LotId);
    Assert.Equal((10, 0), ((await _wallet.GetWalletAsync(third.Id)).AvailablePoints, await _dbContext.LoyaltyDebts.CountAsync()));
    await AssertReconciledAsync();
  }

  // ── Merge (ADR-0013) ───────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task Merge_MovesUsableLotsWithTheirExpiry_LeavesExpiredOnes_AndSettlesTheMovedDebtSoonestExpiryFirst()
  {
    await SetProgramAsync(enabled: true, validityDays: 365);
    var source = await CreateCustomerAsync("Origen");
    var target = await CreateCustomerAsync("Destino");

    _clock.Advance(TimeSpan.FromDays(-400));
    await CreditAsync(source.Id, 5); // expired by today
    _clock.Advance(TimeSpan.FromDays(400));
    await SetProgramAsync(enabled: true, validityDays: 10);
    await CreditAsync(source.Id, 20); // expires in 10 days
    await SetProgramAsync(enabled: true, validityDays: 100);
    await CreditAsync(target.Id, 30);
    await SeedDebtAsync(source.Id, 15);

    var preview = await _customers.GetMergePreviewAsync(target.Id, source.Id);
    Assert.Equal(
        (20, 15, 30, 0, 35, 0, 5),
        (preview.Loyalty!.SourceAvailablePoints, preview.Loyalty.SourceDebtPoints, preview.Loyalty.TargetAvailablePoints,
            preview.Loyalty.TargetDebtPoints, preview.Loyalty.ResultAvailablePoints, preview.Loyalty.ResultDebtPoints,
            preview.Loyalty.SourceExpiredPointsNotMoved));
    var moving = Assert.Single(preview.Loyalty.LotsToTransfer);
    Assert.Equal((20, Today.AddDays(9), true), (moving.Points, moving.ExpiresOn, moving.ExpiresSoon));

    await _customers.MergeAsync(_admin.Id, target.Id, new CustomerMergeRequest(source.Id, preview.PreviewToken));

    var wallet = await _wallet.GetWalletAsync(target.Id);
    Assert.Equal((35, 0), (wallet.AvailablePoints, wallet.DebtPoints));
    var moved = await _dbContext.LoyaltyLots.SingleAsync(lot => lot.CustomerId == target.Id && lot.OriginLotId != null);
    var original = await _dbContext.LoyaltyLots.SingleAsync(lot => lot.Id == moved.OriginLotId);
    Assert.Equal((original.ExpiresOn, original.AccruedOn, original.ValidityDays, source.Id), (moved.ExpiresOn, moved.AccruedOn, moved.ValidityDays, moved.OriginCustomerId!.Value));
    Assert.Equal((20, 5), (moved.InitialPoints, moved.RemainingPoints)); // 15 settled the debt: it expires first
    Assert.Equal(30, (await _dbContext.LoyaltyLots.SingleAsync(lot => lot.CustomerId == target.Id && lot.OriginLotId == null)).RemainingPoints);
    Assert.Equal(5, (await _dbContext.LoyaltyLots.SingleAsync(lot => lot.CustomerId == source.Id && lot.InitialPoints == 5)).RemainingPoints);
    var sourceDebt = await _dbContext.LoyaltyDebts.SingleAsync(debt => debt.CustomerId == source.Id);
    var movedDebt = await _dbContext.LoyaltyDebts.SingleAsync(debt => debt.CustomerId == target.Id);
    Assert.Equal((0, 15, 0, sourceDebt.Id), (sourceDebt.OutstandingPoints, movedDebt.Amount, movedDebt.OutstandingPoints, movedDebt.OriginDebtId!.Value));
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task Merge_MovesOnlyTheOutstandingDebt_AndLeavesTheUncoveredPartAsDebtOfTheTarget()
  {
    await SetProgramAsync(enabled: true);
    var source = await CreateCustomerAsync("Origen");
    var target = await CreateCustomerAsync("Destino");
    await SeedDebtAsync(source.Id, 100);
    await CreditAsync(source.Id, 30); // settles 30 of the 100 at once
    await CreditAsync(target.Id, 20);

    await _customers.MergeWithPreviewAsync(_admin.Id, target.Id, source.Id);

    var wallet = await _wallet.GetWalletAsync(target.Id);
    Assert.Equal((0, 50), (wallet.AvailablePoints, wallet.DebtPoints));
    var movedDebt = Assert.Single(wallet.Debts);
    Assert.Equal((70, 50), (movedDebt.Amount, movedDebt.OutstandingPoints));
    Assert.Equal(100, await _dbContext.LoyaltyMovements
        .Where(movement => movement.Type == LoyaltyMovementType.DebtSettlement).SumAsync(movement => movement.Points) + 50);
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task Merge_IsRefused_WhenAnythingChangedAfterThePreview_AndANewPreviewWorks()
  {
    await SetProgramAsync(enabled: true);
    var source = await CreateCustomerAsync("Origen");
    var target = await CreateCustomerAsync("Destino");
    await CreditAsync(source.Id, 20);
    var preview = await _customers.GetMergePreviewAsync(target.Id, source.Id);

    await CreditAsync(source.Id, 5);
    await Assert.ThrowsAsync<ConflictException>(() =>
        _customers.MergeAsync(_admin.Id, target.Id, new CustomerMergeRequest(source.Id, preview.PreviewToken)));
    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _customers.MergeAsync(_admin.Id, target.Id, new CustomerMergeRequest(source.Id)));
    Assert.Null((await _dbContext.Customers.SingleAsync(customer => customer.Id == source.Id)).MergedIntoCustomerId);

    await _customers.MergeWithPreviewAsync(_admin.Id, target.Id, source.Id);
    Assert.Equal(25, (await _wallet.GetWalletAsync(target.Id)).AvailablePoints);
  }

  [Fact]
  public async Task TheChainResolver_FollowsMerges_AndFailsExplicitlyOnACycleOrATooLongChain()
  {
    var profiles = new List<CustomerProfile>();
    for (var index = 0; index < LoyaltyWalletService.MaxMergeChainDepth + 2; index++)
    {
      profiles.Add(new CustomerProfile($"Perfil {index}", "3001234567", $"perfil{index}@example.com", _admin.Id, DateTime.UtcNow));
    }

    _dbContext.Customers.AddRange(profiles);
    await _dbContext.SaveChangesAsync();
    for (var index = 0; index < profiles.Count - 1; index++)
    {
      _dbContext.Entry(profiles[index]).Property(profile => profile.MergedIntoCustomerId).CurrentValue = profiles[index + 1].Id;
    }

    await _dbContext.SaveChangesAsync();

    Assert.Equal(profiles[^1].Id, await _wallet.ResolveLiveCustomerIdAsync(profiles[^3].Id, CancellationToken.None));
    var tooLong = await Assert.ThrowsAsync<ConflictException>(() => _wallet.ResolveLiveCustomerIdAsync(profiles[0].Id, CancellationToken.None));
    Assert.Contains("supera", tooLong.Message);

    _dbContext.Entry(profiles[^1]).Property(profile => profile.MergedIntoCustomerId).CurrentValue = profiles[^2].Id;
    await _dbContext.SaveChangesAsync();
    var cycle = await Assert.ThrowsAsync<ConflictException>(() => _wallet.ResolveLiveCustomerIdAsync(profiles[^2].Id, CancellationToken.None));
    Assert.Contains("ciclo", cycle.Message);
    await Assert.ThrowsAsync<ConflictException>(() => _wallet.GetWalletAsync(profiles[^2].Id));
  }

  [Fact]
  public async Task AMovedDebtKeepsItsAge_SoTheSourcesOlderDebtIsSettledBeforeTheTargetsNewerOne()
  {
    var source = await CreateCustomerAsync("Origen");
    var target = await CreateCustomerAsync("Destino");
    _clock.Advance(TimeSpan.FromDays(-10));
    var oldDebtId = await SeedDebtAsync(source.Id, 15);
    _clock.Advance(TimeSpan.FromDays(9));
    var newDebtId = await SeedDebtAsync(target.Id, 10);
    _clock.Advance(TimeSpan.FromDays(1));
    var oldIncurredAt = (await _dbContext.LoyaltyDebts.AsNoTracking().SingleAsync(debt => debt.Id == oldDebtId)).IncurredAt;

    await _customers.MergeWithPreviewAsync(_admin.Id, target.Id, source.Id);
    var wallet = await CreditAsync(target.Id, 20); // not enough for both (25)

    var moved = await _dbContext.LoyaltyDebts.AsNoTracking().SingleAsync(debt => debt.OriginDebtId == oldDebtId);
    var newer = await _dbContext.LoyaltyDebts.AsNoTracking().SingleAsync(debt => debt.Id == newDebtId);
    Assert.Equal(oldIncurredAt, moved.IncurredAt);
    Assert.True(moved.CreatedAt > moved.IncurredAt); // the row is the merge's; its age is the original debt's
    Assert.Equal((0, 5), (moved.OutstandingPoints, newer.OutstandingPoints));
    Assert.Equal((0, 5), (wallet.AvailablePoints, wallet.DebtPoints));
    Assert.Equal([moved.Id, newDebtId], wallet.Debts.Select(debt => debt.Id)); // listed by age, oldest first
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task TheAgeOfADebtSurvivesTwoMerges_WithoutTouchingTheMovementsOfTheEarlierOnes()
  {
    var first = await CreateCustomerAsync("Uno");
    var second = await CreateCustomerAsync("Dos");
    var third = await CreateCustomerAsync("Tres");
    _clock.Advance(TimeSpan.FromDays(-10));
    var oldDebtId = await SeedDebtAsync(first.Id, 15);
    _clock.Advance(TimeSpan.FromDays(9));
    var newDebtId = await SeedDebtAsync(third.Id, 10);
    _clock.Advance(TimeSpan.FromDays(1));
    var oldIncurredAt = (await _dbContext.LoyaltyDebts.AsNoTracking().SingleAsync(debt => debt.Id == oldDebtId)).IncurredAt;

    await _customers.MergeWithPreviewAsync(_admin.Id, second.Id, first.Id);
    var historyAfterFirstMerge = await MovementHistoryAsync();
    _clock.Advance(TimeSpan.FromHours(2));
    await _customers.MergeWithPreviewAsync(_admin.Id, third.Id, second.Id);

    // The movements written by the first merge (and before) are still there, unchanged.
    Assert.Equal(historyAfterFirstMerge, (await MovementHistoryAsync()).Take(historyAfterFirstMerge.Count));
    var middle = await _dbContext.LoyaltyDebts.AsNoTracking().SingleAsync(debt => debt.OriginDebtId == oldDebtId);
    var last = await _dbContext.LoyaltyDebts.AsNoTracking().SingleAsync(debt => debt.OriginDebtId == middle.Id);
    Assert.Equal((second.Id, third.Id, first.Id, first.Id), (middle.CustomerId, last.CustomerId, middle.OriginCustomerId!.Value, last.OriginCustomerId!.Value));
    Assert.Equal((oldIncurredAt, oldIncurredAt), (middle.IncurredAt, last.IncurredAt));
    Assert.True(last.CreatedAt > middle.CreatedAt);

    await CreditAsync(third.Id, 20);
    var newer = await _dbContext.LoyaltyDebts.AsNoTracking().SingleAsync(debt => debt.Id == newDebtId);
    var moved = await _dbContext.LoyaltyDebts.AsNoTracking().SingleAsync(debt => debt.Id == last.Id);
    Assert.Equal((0, 5), (moved.OutstandingPoints, newer.OutstandingPoints));
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task WithAOneDayValidity_PointsLastUntilTheEndOfTheAccrualDayInBogota()
  {
    // 2026-11-03 17:00 UTC = 12:00 Bogotá. One day of validity: last valid day 2026-11-03 (inclusive).
    await SetProgramAsync(enabled: false, validityDays: 1);
    var customer = await CreateCustomerAsync("Carla");
    var lot = (await CreditAsync(customer.Id, 10)).Lots.Single();
    Assert.Equal((new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 3)), (lot.AccruedOn, lot.ExpiresOn));

    _clock.Advance(new TimeSpan(11, 59, 59)); // 2026-11-04 04:59:59 UTC = 2026-11-03 23:59:59 Bogotá
    Assert.Equal(10, (await _wallet.GetWalletAsync(customer.Id)).AvailablePoints);
    Assert.Equal(0, await _wallet.ExpireDueLotsAsync(CancellationToken.None));

    _clock.Advance(TimeSpan.FromSeconds(1)); // 2026-11-04 05:00:00 UTC = 2026-11-04 00:00 Bogotá
    var expired = await _wallet.GetWalletAsync(customer.Id);
    Assert.Equal((0, true), (expired.AvailablePoints, expired.Lots.Single().IsExpired));
    Assert.Equal(1, await _wallet.ExpireDueLotsAsync(CancellationToken.None));
    await AssertReconciledAsync();
  }

  // ── Adjustments, debts and expiration ──────────────────────────────────────────────────────

  [Fact]
  public async Task Adjustments_WorkWithTheProgramDisabled_DebitSoonestExpiryFirst_NeverCreateDebt_AndAreIdempotent()
  {
    await SetProgramAsync(enabled: false, validityDays: 10);
    var customer = await CreateCustomerAsync("Carla");
    await CreditAsync(customer.Id, 20); // expires first
    await SetProgramAsync(enabled: false, validityDays: 365);
    await CreditAsync(customer.Id, 30);

    await Assert.ThrowsAsync<ConflictException>(() => DebitAsync(customer.Id, 51, Guid.NewGuid()));
    var key = Guid.NewGuid();
    var debited = await DebitAsync(customer.Id, 25, key);
    var replayed = await DebitAsync(customer.Id, 25, key);

    Assert.Equal((25, 25, 0), (debited.AvailablePoints, replayed.AvailablePoints, debited.DebtPoints));
    var lots = debited.Lots.OrderBy(lot => lot.ExpiresOn).ToArray();
    Assert.Equal((0, 25), (lots[0].RemainingPoints, lots[1].RemainingPoints));
    Assert.Equal(2, await _dbContext.LoyaltyMovements.CountAsync(movement => movement.Type == LoyaltyMovementType.AdjustmentDebit));
    await Assert.ThrowsAsync<ConflictException>(() => DebitAsync(customer.Id, 5, key));
    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _wallet.AdjustAsync(_admin.Id, customer.Id, new LoyaltyAdjustmentRequest("Debit", 5, "no", Guid.NewGuid())));
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task AdjustingAMergedProfile_IsRefused()
  {
    var source = await CreateCustomerAsync("Origen");
    var target = await CreateCustomerAsync("Destino");
    await _customers.MergeWithPreviewAsync(_admin.Id, target.Id, source.Id);

    await Assert.ThrowsAsync<ConflictException>(() => CreditAsync(source.Id, 10));
    var merged = await _wallet.GetWalletAsync(source.Id);
    Assert.Equal((true, target.Id), (merged.IsMerged, merged.LiveCustomerId));
  }

  [Fact]
  public async Task NewPointsSettleAnExistingDebtFirst()
  {
    var customer = await CreateCustomerAsync("Carla");
    await SeedDebtAsync(customer.Id, 15);

    var wallet = await CreditAsync(customer.Id, 10);
    Assert.Equal((0, 5), (wallet.AvailablePoints, wallet.DebtPoints));
    wallet = await CreditAsync(customer.Id, 10);
    Assert.Equal((5, 0), (wallet.AvailablePoints, wallet.DebtPoints));
    Assert.Equal(2, await _dbContext.LoyaltyMovements.CountAsync(movement => movement.Type == LoyaltyMovementType.DebtSettlement));
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task ExpiredPoints_StopCountingAfterTheLastValidDay_AndTheJobRecordsEachExpirationOnce()
  {
    await SetProgramAsync(enabled: true, validityDays: 2);
    var customer = await CreateCustomerAsync("Carla");
    await CreditAsync(customer.Id, 20);
    var lastValidDay = Today.AddDays(1);

    _clock.Advance(TimeSpan.FromDays(1));
    Assert.Equal((lastValidDay, 20), (Today, (await _wallet.GetWalletAsync(customer.Id)).AvailablePoints));
    Assert.Equal(0, await _wallet.ExpireDueLotsAsync(CancellationToken.None));

    _clock.Advance(TimeSpan.FromDays(1));
    var beforeJob = await _wallet.GetWalletAsync(customer.Id);
    Assert.Equal((0, true, 20), (beforeJob.AvailablePoints, beforeJob.Lots.Single().IsExpired, beforeJob.Lots.Single().RemainingPoints));

    Assert.Equal(1, await _wallet.ExpireDueLotsAsync(CancellationToken.None));
    Assert.Equal(0, await _wallet.ExpireDueLotsAsync(CancellationToken.None));
    var expiration = await _dbContext.LoyaltyMovements.SingleAsync(movement => movement.Type == LoyaltyMovementType.Expiration);
    Assert.Equal((20, null), (expiration.Points, expiration.PerformedByUserId));
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task MyPoints_ShowOnlyTheCallersLiveProfile()
  {
    var user = new User("Carla Cliente", "carla@example.com", "hash", DateTime.UtcNow);
    _dbContext.Users.Add(user);
    await _dbContext.SaveChangesAsync();
    var mine = await CreateCustomerAsync("Carla Cliente");
    var profile = await _dbContext.Customers.SingleAsync(customer => customer.Id == mine.Id);
    profile.LinkAccount(user.Id, _admin.Id, DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();
    var other = await CreateCustomerAsync("Otra");
    await CreditAsync(mine.Id, 12);
    await CreditAsync(other.Id, 99);

    var view = await _wallet.GetMineAsync(user.Id);
    Assert.Equal((true, 12, 1), (view.HasProfile, view.AvailablePoints, view.Lots.Count));
    Assert.Equal("AdjustmentCredit", Assert.Single(view.Movements).Type);
    Assert.False((await _wallet.GetMineAsync(_admin.Id)).HasProfile);
  }

  // ── Helpers ────────────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// The projections match the history: each lot and debt has exactly one opening movement equal to its initial
  /// amount, and its stored balance is the signed sum of its movements, the opening counted once.
  /// </summary>
  private async Task AssertReconciledAsync()
  {
    var movements = await _dbContext.LoyaltyMovements.AsNoTracking().ToListAsync();
    foreach (var lot in await _dbContext.LoyaltyLots.AsNoTracking().ToListAsync())
    {
      var own = movements.Where(movement => movement.LotId == lot.Id).ToList();
      Assert.Equal(lot.InitialPoints, Assert.Single(own, movement => LoyaltyMovement.LotSign(movement.Type) > 0).Points);
      Assert.Equal(lot.RemainingPoints, own.Sum(movement => LoyaltyMovement.LotSign(movement.Type) * movement.Points));
    }

    foreach (var debt in await _dbContext.LoyaltyDebts.AsNoTracking().ToListAsync())
    {
      var own = movements.Where(movement => movement.DebtId == debt.Id).ToList();
      Assert.Equal(debt.Amount, Assert.Single(own, movement => LoyaltyMovement.DebtSign(movement.Type) > 0).Points);
      Assert.Equal(debt.OutstandingPoints, own.Sum(movement => LoyaltyMovement.DebtSign(movement.Type) * movement.Points));
    }
  }

  private async Task<List<string>> MovementHistoryAsync()
      => (await _dbContext.LoyaltyMovements.AsNoTracking().ToListAsync())
          .OrderBy(movement => movement.PerformedAt).ThenBy(movement => movement.Id)
          .Select(movement => $"{movement.Id}|{movement.CustomerId}|{movement.Type}|{movement.Points}|{movement.LotId}|{movement.DebtId}|{movement.PerformedAt:O}")
          .ToList();

  private Task<LoyaltyWalletView> CreditAsync(Guid customerId, int points)
      => _wallet.AdjustAsync(_admin.Id, customerId, new LoyaltyAdjustmentRequest("Credit", points, "Bono de prueba", Guid.NewGuid()));

  private Task<LoyaltyWalletView> DebitAsync(Guid customerId, int points, Guid key)
      => _wallet.AdjustAsync(_admin.Id, customerId, new LoyaltyAdjustmentRequest("Debit", points, "Corrección de prueba", key));

  /// <summary>A debt as a future reversal would open it (stage 4 creates none of its own).</summary>
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

  private async Task SetProgramAsync(bool enabled, int validityDays = 365)
  {
    var settings = await _dbContext.LoyaltySettings.SingleAsync();
    settings.Update(enabled, validityDays, _admin.Id, DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();
  }

  private Task<CustomerDetailView> CreateCustomerAsync(string name)
      => _customers.CreateAsync(_admin.Id, new CustomerCreateRequest(name, "3001234567", $"{Guid.NewGuid():N}@example.com"));

  private async Task<CustomerMembershipDetailView> SellAsync(Guid customerId, int planPoints, string planName = "Oro")
  {
    var plan = await _plans.CreateAsync(new MembershipPlanUpsertRequest(
        $"{planName} {Guid.NewGuid():N}"[..20], null, 250000, "Pool", 10, 90, Terms, null, [new(_haircut.Id, null)], [], planPoints));
    return await _memberships.SellAsync(_admin.Id, customerId, new MembershipSaleRequest(plan.Id, Today, ActivateNow: true));
  }

  private async Task<Guid> AddIncomeAsync()
  {
    var income = IncomeEntry.CreateMembershipSale(250000, Today, _admin.Id, DateTime.UtcNow);
    _dbContext.IncomeEntries.Add(income);
    await _dbContext.SaveChangesAsync();
    return income.Id;
  }

  private sealed class FixedCurrentUserAccessor(Guid userId) : ICurrentUserAccessor
  {
    public Guid? UserId { get; } = userId;
  }

  private sealed class MutableClock(DateTimeOffset utcNow) : TimeProvider
  {
    private DateTimeOffset _utcNow = utcNow;

    public void Advance(TimeSpan by) => _utcNow += by;

    public override DateTimeOffset GetUtcNow() => _utcNow;
  }
}
