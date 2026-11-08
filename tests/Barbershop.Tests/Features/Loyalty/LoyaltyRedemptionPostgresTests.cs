using System.Data.Common;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Loyalty.Redemptions;
using Barbershop.Application.ServiceRecords;
using Barbershop.Domain.Common;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.ServiceRecords;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Customers;
using Barbershop.Infrastructure.Loyalty;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.ServiceRecords;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Barbershop.Tests.Features.Loyalty;

/// <summary>
/// Redemptions on real PostgreSQL (ADR-0015): the unique indexes on movements with the partial-lot release after
/// expiry, xmin on the catalog, and races forced deterministically with the global lock order service record →
/// customer profile → redemption: the first operation is held right after taking its locks, the second is observed
/// waiting in pg_stat_activity, then the first is released. Points are never reserved twice beyond the balance,
/// released and consumed at most once, and everything reconciles.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "LoyaltyRedemptionPostgres")]
public sealed class LoyaltyRedemptionPostgresTests
{
  private const string ProfileLock = "public.customers WHERE";
  private const string RedemptionLock = "public.loyalty_redemptions WHERE \"Id\" =";
  private const string JobRedemptionLock = "\"Status\" IN ('Requested', 'Approved')";
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
  private static readonly LoyaltyMovementType[] Openings =
      [LoyaltyMovementType.Accrual, LoyaltyMovementType.MergeTransferIn, LoyaltyMovementType.AdjustmentCredit];

  private readonly PostgresContainerFixture _postgres;
  private readonly MutableClock _clock = new(DateTimeOffset.UtcNow);

  public LoyaltyRedemptionPostgresTests(PostgresContainerFixture postgres) => _postgres = postgres;

  [Fact]
  public async Task ReservingPartOfALot_ExpiringTheRest_ThenCancelling_ExpiresTheReleasedPointsWithoutFailing()
  {
    if (!_postgres.IsAvailable) return; // Only with PostgresContainerFixture.AllowSkipVariable set on purpose.
    var seed = await SeedAsync(lotPoints: 50, validityDays: 2);
    var reward = await CreateRewardAsync(seed, cost: 30);
    var requested = await RequestAsync(seed, reward.Id);

    _clock.Advance(TimeSpan.FromDays(3));
    await using (var job = CreateContext(null))
    {
      Assert.Equal(1, await new LoyaltyWalletService(job, _clock).ExpireDueLotsAsync(CancellationToken.None));
    }

    // A second Expiration of the same lot (tied to the redemption) must pass the unique indexes.
    await using (var context = CreateContext(null))
    {
      await Svc(context).Redemptions.CancelMineAsync(seed.CustomerUserId, requested.Id, new MyLoyaltyRedemptionCancelRequest(null));
    }

    await using var verify = CreateContext(null);
    var lot = await verify.LoyaltyLots.AsNoTracking().SingleAsync();
    Assert.Equal((0, 0), (lot.RemainingPoints, lot.ReservedPoints));
    Assert.Equal(0, (await Svc(verify).Wallet.GetWalletAsync(seed.CustomerId)).AvailablePoints);
    Assert.Equal(2, await verify.LoyaltyMovements.CountAsync(movement => movement.Type == LoyaltyMovementType.Expiration));
    await using (var job = CreateContext(null))
    {
      Assert.Equal(0, await new LoyaltyWalletService(job, _clock).ExpireDueLotsAsync(CancellationToken.None));
    }

    await AssertReconciledAsync();
  }

  [Fact]
  public async Task AReleaseCannotBeRecordedTwiceForTheSameLotAndRedemption()
  {
    if (!_postgres.IsAvailable) return;
    var seed = await SeedAsync(lotPoints: 50);
    var reward = await CreateRewardAsync(seed, cost: 30);
    var requested = await RequestAsync(seed, reward.Id);
    await using (var context = CreateContext(null))
    {
      await Svc(context).Redemptions.CancelMineAsync(seed.CustomerUserId, requested.Id, new MyLoyaltyRedemptionCancelRequest(null));
    }

    await using var duplicate = CreateContext(null);
    var lot = await duplicate.LoyaltyLots.AsNoTracking().SingleAsync();
    duplicate.LoyaltyMovements.Add(new LoyaltyMovement(
        seed.CustomerId, LoyaltyMovementType.RedemptionRelease, 30, DateTime.UtcNow, lotId: lot.Id, redemptionId: requested.Id));
    var exception = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
    Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
  }

  [Fact]
  public async Task TwoRequestsOverTheBalance_AreSerializedByTheProfileLock_AndOnlyOneReserves()
  {
    if (!_postgres.IsAvailable) return;
    var seed = await SeedAsync(lotPoints: 50);
    var reward = await CreateRewardAsync(seed, cost: 30);

    var (first, second) = await RaceAsync(
        ProfileLock,
        services => services.Redemptions.RequestAsync(seed.CustomerUserId, new LoyaltyRedemptionRequest(reward.Id, Guid.NewGuid())),
        services => services.Redemptions.RequestAsync(seed.CustomerUserId, new LoyaltyRedemptionRequest(reward.Id, Guid.NewGuid())));

    Assert.Null(first);
    Assert.IsType<ConflictException>(second);
    await using var verify = CreateContext(null);
    Assert.Equal(1, await verify.LoyaltyRedemptions.CountAsync());
    var lot = await verify.LoyaltyLots.AsNoTracking().SingleAsync();
    Assert.Equal((20, 30), (lot.RemainingPoints, lot.ReservedPoints));
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task AnApprovalHoldingTheRedemption_MakesAStaleCancellationWaitAndFail_WithoutReleasing()
  {
    if (!_postgres.IsAvailable) return;
    var seed = await SeedAsync(lotPoints: 50);
    var reward = await CreateRewardAsync(seed, cost: 30);
    var requested = await RequestAsync(seed, reward.Id);
    uint version;
    await using (var read = CreateContext(null))
    {
      version = (await Svc(read).Redemptions.GetAsync(requested.Id)).Version;
    }

    var (approve, cancel) = await RaceAsync(
        RedemptionLock,
        services => services.Redemptions.ApproveAsync(seed.AdminId, requested.Id, new LoyaltyRedemptionDecisionRequest(version)),
        services => services.Redemptions.CancelAsync(seed.AdminId, requested.Id, new LoyaltyRedemptionReasonRequest(version, "Ya no aplica")));

    Assert.Null(approve);
    Assert.IsType<ConflictException>(cancel);
    await using var verify = CreateContext(null);
    Assert.Equal(LoyaltyRedemptionStatus.Approved, (await verify.LoyaltyRedemptions.AsNoTracking().SingleAsync()).Status);
    Assert.Equal(0, await verify.LoyaltyMovements.CountAsync(movement => movement.Type == LoyaltyMovementType.RedemptionRelease));
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task ARequestHoldingTheProfile_MakesAMergeWait_ThenThePendingRedemptionBlocksIt()
  {
    if (!_postgres.IsAvailable) return;
    var seed = await SeedAsync(lotPoints: 50);
    var reward = await CreateRewardAsync(seed, cost: 30);
    string token;
    await using (var read = CreateContext(null))
    {
      token = (await Svc(read).Customers.GetMergePreviewAsync(seed.CustomerId, seed.DuplicateId)).PreviewToken;
    }

    var (request, merge) = await RaceAsync(
        ProfileLock,
        services => services.Redemptions.RequestAsync(seed.CustomerUserId, new LoyaltyRedemptionRequest(reward.Id, Guid.NewGuid())),
        services => services.Customers.MergeAsync(seed.AdminId, seed.CustomerId, new CustomerMergeRequest(seed.DuplicateId, token)));

    Assert.Null(request);
    var conflict = Assert.IsType<ConflictException>(merge);
    Assert.Contains("canje", conflict.Message);
    await using var verify = CreateContext(null);
    Assert.Null((await verify.Customers.AsNoTracking().SingleAsync(customer => customer.Id == seed.DuplicateId)).MergedIntoCustomerId);
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task AServiceRecordDeliveringTheReward_MakesACustomerCancellationWaitAndFail_ConsumingOnce()
  {
    if (!_postgres.IsAvailable) return;
    var seed = await SeedAsync(lotPoints: 50);
    var reward = await CreateRewardAsync(seed, cost: 30, serviceId: seed.HaircutId);
    var requested = await RequestAsync(seed, reward.Id);
    await using (var approve = CreateContext(null))
    {
      var redemptions = Svc(approve).Redemptions;
      await redemptions.ApproveAsync(seed.AdminId, requested.Id, new LoyaltyRedemptionDecisionRequest((await redemptions.GetAsync(requested.Id)).Version));
    }

    var record = await SeedPendingRecordAsync(seed);
    var (deliver, cancel) = await RaceAsync(
        RedemptionLock,
        services => services.Records.ApproveAsync(seed.AdminId, record.Id, new ServiceRecordApproveRequest(
            record.Version, "LoyaltyReward", seed.HaircutId, seed.StaffProfileId, seed.CustomerId, record.PerformedOn, null, null, false, null, requested.Id)),
        services => services.Redemptions.CancelMineAsync(seed.CustomerUserId, requested.Id, new MyLoyaltyRedemptionCancelRequest(null)));

    Assert.Null(deliver);
    Assert.IsType<ConflictException>(cancel);
    await using var verify = CreateContext(null);
    var redemption = await verify.LoyaltyRedemptions.AsNoTracking().SingleAsync();
    Assert.Equal((LoyaltyRedemptionStatus.Delivered, record.Id), (redemption.Status, redemption.ServiceRecordId!.Value));
    Assert.Equal(1, await verify.LoyaltyMovements.CountAsync(movement => movement.Type == LoyaltyMovementType.RedemptionConsumed));
    Assert.Equal(0, await verify.LoyaltyMovements.CountAsync(movement => movement.Type == LoyaltyMovementType.RedemptionRelease));
    var stored = await verify.ServiceRecords.AsNoTracking().SingleAsync(candidate => candidate.Id == record.Id);
    Assert.Equal((ServiceRecordOperationType.LoyaltyReward, 17500), (stored.OperationType!.Value, stored.RewardProfessionalCommission!.Value));
    Assert.Equal(0, await verify.IncomeEntries.CountAsync());
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task TheExpirationJobHoldingDueRedemptions_MakesADeliveryWaitAndFail_ReleasingOnce()
  {
    if (!_postgres.IsAvailable) return;
    var seed = await SeedAsync(lotPoints: 50);
    var reward = await CreateRewardAsync(seed, cost: 30);
    var requested = await RequestAsync(seed, reward.Id);
    uint version;
    await using (var approve = CreateContext(null))
    {
      var redemptions = Svc(approve).Redemptions;
      version = (await redemptions.ApproveAsync(seed.AdminId, requested.Id, new LoyaltyRedemptionDecisionRequest((await redemptions.GetAsync(requested.Id)).Version))).Version;
    }

    // Past the 30-day delivery deadline.
    _clock.Advance(TimeSpan.FromDays(31));
    var (job, deliver) = await RaceAsync(
        JobRedemptionLock,
        services => services.Redemptions.ExpireDueRedemptionsAsync(CancellationToken.None),
        services => services.Redemptions.DeliverAsync(seed.AdminId, requested.Id, new LoyaltyRedemptionDecisionRequest(version)));

    Assert.Null(job);
    Assert.IsType<ConflictException>(deliver);
    await using var verify = CreateContext(null);
    Assert.Equal(LoyaltyRedemptionStatus.Expired, (await verify.LoyaltyRedemptions.AsNoTracking().SingleAsync()).Status);
    Assert.Equal(1, await verify.LoyaltyMovements.CountAsync(movement => movement.Type == LoyaltyMovementType.RedemptionRelease));
    Assert.Equal(0, await verify.LoyaltyMovements.CountAsync(movement => movement.Type == LoyaltyMovementType.RedemptionConsumed));
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task EditingARewardWithAStaleVersion_IsAConflict()
  {
    if (!_postgres.IsAvailable) return;
    var seed = await SeedAsync(lotPoints: 1);
    var reward = await CreateRewardAsync(seed, cost: 30);
    await using (var first = CreateContext(null))
    {
      await Svc(first).Redemptions.UpdateAsync(seed.AdminId, reward.Id, new LoyaltyRewardUpsertRequest("Gorra roja", null, "Other", null, 30, true, reward.Version));
    }

    await using var second = CreateContext(null);
    await Assert.ThrowsAsync<ConflictException>(() => Svc(second).Redemptions.UpdateAsync(
        seed.AdminId, reward.Id, new LoyaltyRewardUpsertRequest("Gorra azul", null, "Other", null, 30, true, reward.Version)));
  }

  // ── Helpers ────────────────────────────────────────────────────────────────────────────────

  private async Task<(Exception? Holder, Exception? Contender)> RaceAsync(
      string pauseAfter, Func<Services, Task> holder, Func<Services, Task> contender)
  {
    var gate = new LockGate(pauseAfter);
    await using var holderContext = CreateContext(gate);
    await using var contenderContext = CreateContext(null);

    var holding = Task.Run(() => Capture(() => holder(Svc(holderContext))));
    await gate.Held.WaitAsync(Timeout);
    var contending = Task.Run(() => Capture(() => contender(Svc(contenderContext))));
    await WaitUntilASessionWaitsOnALockAsync();
    Assert.False(contending.IsCompleted);

    gate.Release();
    return (await holding.WaitAsync(Timeout), await contending.WaitAsync(Timeout));
  }

  private static async Task<Exception?> Capture(Func<Task> action)
  {
    try
    {
      await action();
      return null;
    }
    catch (Exception exception)
    {
      return exception;
    }
  }

  private async Task WaitUntilASessionWaitsOnALockAsync()
  {
    await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
    await connection.OpenAsync();
    var deadline = DateTime.UtcNow + Timeout;
    while (true)
    {
      await using var command = new NpgsqlCommand(
          "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE '%FOR UPDATE%'", connection);
      if ((long)(await command.ExecuteScalarAsync())! > 0)
      {
        return;
      }

      Assert.True(DateTime.UtcNow < deadline, "The second operation never waited on a lock.");
      await Task.Delay(50);
    }
  }

  /// <summary>Free, reserved and consumed points of every lot against its movements; each allocation closed at most once.</summary>
  private async Task AssertReconciledAsync()
  {
    await using var context = CreateContext(null);
    var movements = await context.LoyaltyMovements.AsNoTracking().ToListAsync();
    foreach (var lot in await context.LoyaltyLots.AsNoTracking().ToListAsync())
    {
      var own = movements.Where(movement => movement.LotId == lot.Id).ToList();
      Assert.Equal(lot.InitialPoints, Assert.Single(own, movement => Openings.Contains(movement.Type)).Points);
      Assert.Equal(lot.RemainingPoints, own.Sum(movement => LoyaltyMovement.LotSign(movement.Type) * movement.Points));
      Assert.Equal(lot.ReservedPoints, own.Sum(movement => LoyaltyMovement.ReservedSign(movement.Type) * movement.Points));
    }

    foreach (var allocation in await context.LoyaltyRedemptionAllocations.AsNoTracking().ToListAsync())
    {
      var closing = movements.Count(movement => movement.RedemptionId == allocation.RedemptionId && movement.LotId == allocation.LotId
          && movement.Type is LoyaltyMovementType.RedemptionRelease or LoyaltyMovementType.RedemptionConsumed);
      Assert.True(closing <= 1);
    }
  }

  private sealed record Seed(
      Guid AdminId, Guid CustomerUserId, Guid CustomerId, Guid DuplicateId, Guid HaircutId, Guid StaffProfileId);

  /// <summary>Program enabled; a customer linked to an account with one lot, a duplicate profile, a haircut and a barber.</summary>
  private async Task<Seed> SeedAsync(int lotPoints, int validityDays = 365)
  {
    await using var context = CreateContext(null);
    await context.Database.EnsureDeletedAsync();
    await context.Database.MigrateAsync();

    var admin = new User("Admin Principal", "admin@example.com", "hash", DateTime.UtcNow);
    var customerUser = new User("Carla Cliente", "carla@example.com", "hash", DateTime.UtcNow);
    var barberUser = new User("Alex Barbero", "alex@example.com", "hash", DateTime.UtcNow);
    var haircut = new Service("Corte", 25000, DateTime.UtcNow, 30);
    var barber = new StaffProfile(barberUser.Id, "Alex", 30, DateTime.UtcNow);
    context.Users.AddRange(admin, customerUser, barberUser);
    context.Services.Add(haircut);
    context.StaffProfiles.Add(barber);
    var settings = await context.LoyaltySettings.SingleAsync();
    settings.Update(true, 365, admin.Id, DateTime.UtcNow);
    var profile = new CustomerProfile("Carla Cliente", "3001234567", "carla@example.com", admin.Id, DateTime.UtcNow);
    profile.LinkAccount(customerUser.Id, admin.Id, DateTime.UtcNow);
    var duplicate = new CustomerProfile("Carla Duplicada", "3007654321", "duplicada@example.com", admin.Id, DateTime.UtcNow);
    context.Customers.AddRange(profile, duplicate);
    var now = _clock.GetUtcNow().UtcDateTime;
    var lot = LoyaltyLot.Accrue(profile.Id, LoyaltySourceType.Adjustment, Guid.NewGuid(), lotPoints, now,
        DateOnly.FromDateTime(BogotaClock.ToLocal(now)), validityDays);
    context.LoyaltyLots.Add(lot);
    context.LoyaltyMovements.Add(new LoyaltyMovement(profile.Id, LoyaltyMovementType.AdjustmentCredit, lotPoints, now, lotId: lot.Id, performedByUserId: admin.Id));
    await context.SaveChangesAsync();

    return new Seed(admin.Id, customerUser.Id, profile.Id, duplicate.Id, haircut.Id, barber.Id);
  }

  private async Task<LoyaltyRewardView> CreateRewardAsync(Seed seed, int cost, Guid? serviceId = null)
  {
    await using var context = CreateContext(null);
    return await Svc(context).Redemptions.CreateAsync(seed.AdminId, new LoyaltyRewardUpsertRequest(
        serviceId is null ? "Gorra" : "Corte gratis", null, serviceId is null ? "Other" : "Service", serviceId, cost));
  }

  private async Task<MyLoyaltyRedemptionView> RequestAsync(Seed seed, Guid rewardId)
  {
    await using var context = CreateContext(null);
    return await Svc(context).Redemptions.RequestAsync(seed.CustomerUserId, new LoyaltyRedemptionRequest(rewardId, Guid.NewGuid()));
  }

  private async Task<ServiceRecord> SeedPendingRecordAsync(Seed seed)
  {
    await using var context = CreateContext(null);
    var today = DateOnly.FromDateTime(BogotaClock.ToLocal(_clock.GetUtcNow().UtcDateTime));
    var record = new ServiceRecord(Guid.NewGuid(), 0, null, seed.StaffProfileId, seed.HaircutId, "Corte", today, null, "Carla", null, seed.AdminId, DateTime.UtcNow);
    context.ServiceRecords.Add(record);
    await context.SaveChangesAsync();
    return await context.ServiceRecords.AsNoTracking().SingleAsync(candidate => candidate.Id == record.Id);
  }

  private sealed record Services(
      LoyaltyRedemptionService Redemptions, LoyaltyWalletService Wallet, CustomerManagementService Customers, ServiceRecordsService Records);

  private Services Svc(AppDbContext context)
  {
    var memberships = new MembershipSalesService(context, _clock);
    return new Services(
        new LoyaltyRedemptionService(context, _clock),
        new LoyaltyWalletService(context, _clock),
        new CustomerManagementService(context, _clock),
        new ServiceRecordsService(context, new MembershipLedgerService(context, memberships, _clock), _clock));
  }

  private AppDbContext CreateContext(IInterceptor? interceptor)
  {
    var builder = new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(_postgres.ConnectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName));
    if (interceptor is not null)
    {
      builder.AddInterceptors(interceptor);
    }

    return new AppDbContext(builder.Options);
  }

  private sealed class MutableClock(DateTimeOffset utcNow) : TimeProvider
  {
    private DateTimeOffset _utcNow = utcNow;

    public void Advance(TimeSpan by) => _utcNow += by;

    public override DateTimeOffset GetUtcNow() => _utcNow;
  }

  /// <summary>Holds the first FOR UPDATE command containing the marker, with its locks taken, until released.</summary>
  private sealed class LockGate(string marker) : DbCommandInterceptor
  {
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Held => _held.Task;

    public void Release() => _released.TrySetResult();

    public override async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
      if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal)
          && command.CommandText.Contains(marker, StringComparison.Ordinal)
          && _held.TrySetResult())
      {
        await _released.Task.WaitAsync(Timeout, cancellationToken);
      }

      return result;
    }
  }
}
