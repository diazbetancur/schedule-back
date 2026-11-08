using System.Data.Common;
using Barbershop.Application.Auth;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Loyalty.Wallet;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Domain.Common;
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
using Npgsql;

namespace Barbershop.Tests.Features.Loyalty;

/// <summary>
/// Races between income link, annulment and merge over the same membership and profiles, forced deterministically on
/// real PostgreSQL: the first operation is held right after taking its locks (lock order income → profiles →
/// memberships), the second is observed waiting in pg_stat_activity, then the first is released. Whatever the order,
/// points are credited at most once, withdrawn at most once, never left on an annulled membership, and the stored
/// balances reconcile with the movements.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "LoyaltyWalletPostgres")]
public sealed class LoyaltyWalletPostgresTests
{
  private const string Terms = "La membresía es personal e intransferible.";
  private const string MergeLocksMemberships = "WHERE \"CustomerId\" =";
  private const string LockOfOneMembership = "public.customer_memberships WHERE \"Id\" =";
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

  private readonly PostgresContainerFixture _postgres;

  public LoyaltyWalletPostgresTests(PostgresContainerFixture postgres) => _postgres = postgres;

  [Fact]
  public async Task AMergeHoldingTheProfiles_MakesAWaitingLinkRetry_AndTheRetryCreditsTheSurvivor()
  {
    if (!_postgres.IsAvailable) return; // Only with PostgresContainerFixture.AllowSkipVariable set on purpose.
    var seed = await SeedAsync();
    var token = (await Services(CreateContext(null)).Customers.GetMergePreviewAsync(seed.TargetId, seed.SourceId)).PreviewToken;

    var (merge, link) = await RaceAsync(
        MergeLocksMemberships,
        services => services.Customers.MergeAsync(seed.AdminId, seed.TargetId, new CustomerMergeRequest(seed.SourceId, token)),
        services => services.Memberships.LinkIncomeAsync(seed.AdminId, seed.MembershipId, new MembershipIncomeLinkRequest(seed.IncomeId)));

    Assert.Null(merge);
    Assert.IsType<ConflictException>(link);
    await using (var retry = CreateContext(null))
    {
      await Services(retry).Memberships.LinkIncomeAsync(seed.AdminId, seed.MembershipId, new MembershipIncomeLinkRequest(seed.IncomeId));
    }

    var lot = await SingleLotAsync(seed.MembershipId);
    Assert.Equal((seed.TargetId, 40), (lot.CustomerId, lot.RemainingPoints));
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task ALinkHoldingTheMembership_MakesAMergeWait_ThenTheStalePreviewIsRefused_AndANewOneMovesThePoints()
  {
    if (!_postgres.IsAvailable) return;
    var seed = await SeedAsync();
    var token = (await Services(CreateContext(null)).Customers.GetMergePreviewAsync(seed.TargetId, seed.SourceId)).PreviewToken;

    var (link, merge) = await RaceAsync(
        LockOfOneMembership,
        services => services.Memberships.LinkIncomeAsync(seed.AdminId, seed.MembershipId, new MembershipIncomeLinkRequest(seed.IncomeId)),
        services => services.Customers.MergeAsync(seed.AdminId, seed.TargetId, new CustomerMergeRequest(seed.SourceId, token)));

    Assert.Null(link);
    Assert.IsType<ConflictException>(merge); // the preview did not include the points just credited
    await using (var retry = CreateContext(null))
    {
      await Services(retry).Customers.MergeWithPreviewAsync(seed.AdminId, seed.TargetId, seed.SourceId);
    }

    await using var verify = CreateContext(null);
    var lots = await verify.LoyaltyLots.AsNoTracking().Where(lot => lot.SourceId == seed.MembershipId).ToListAsync();
    Assert.Equal(1, lots.Count(lot => lot.OriginLotId == null));
    Assert.Equal(40, lots.Single(lot => lot.CustomerId == seed.TargetId).RemainingPoints);
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task AMergeHoldingTheProfiles_MakesAWaitingAnnulmentRetry_AndTheRetryWithdrawsInTheSurvivor()
  {
    if (!_postgres.IsAvailable) return;
    var seed = await SeedAsync(linked: true);
    var token = (await Services(CreateContext(null)).Customers.GetMergePreviewAsync(seed.TargetId, seed.SourceId)).PreviewToken;

    var (merge, annul) = await RaceAsync(
        MergeLocksMemberships,
        services => services.Customers.MergeAsync(seed.AdminId, seed.TargetId, new CustomerMergeRequest(seed.SourceId, token)),
        services => services.Memberships.AnnulAsync(seed.AdminId, seed.MembershipId, new MembershipAnnulRequest("Pago devuelto")));

    Assert.Null(merge);
    Assert.IsType<ConflictException>(annul);
    await using (var retry = CreateContext(null))
    {
      await Services(retry).Memberships.AnnulAsync(seed.AdminId, seed.MembershipId, new MembershipAnnulRequest("Pago devuelto"));
    }

    await using var verify = CreateContext(null);
    var withdrawal = await verify.LoyaltyMovements.AsNoTracking().SingleAsync(movement => movement.Type == LoyaltyMovementType.MembershipAnnulmentWithdrawal);
    Assert.Equal((seed.TargetId, 40), (withdrawal.CustomerId, withdrawal.Points));
    Assert.Equal(0, await verify.LoyaltyLots.Where(lot => lot.SourceId == seed.MembershipId).SumAsync(lot => lot.RemainingPoints));
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task AnAnnulmentHoldingTheMembership_MakesAMergeWait_AndNothingIsLeftToMove()
  {
    if (!_postgres.IsAvailable) return;
    var seed = await SeedAsync(linked: true);
    var token = (await Services(CreateContext(null)).Customers.GetMergePreviewAsync(seed.TargetId, seed.SourceId)).PreviewToken;

    var (annul, merge) = await RaceAsync(
        LockOfOneMembership,
        services => services.Memberships.AnnulAsync(seed.AdminId, seed.MembershipId, new MembershipAnnulRequest("Pago devuelto")),
        services => services.Customers.MergeAsync(seed.AdminId, seed.TargetId, new CustomerMergeRequest(seed.SourceId, token)));

    Assert.Null(annul);
    Assert.IsType<ConflictException>(merge);
    await using (var retry = CreateContext(null))
    {
      await Services(retry).Customers.MergeWithPreviewAsync(seed.AdminId, seed.TargetId, seed.SourceId);
    }

    await using var verify = CreateContext(null);
    Assert.Equal(0, await verify.LoyaltyLots.CountAsync(lot => lot.CustomerId == seed.TargetId));
    Assert.Equal(1, await verify.LoyaltyMovements.CountAsync(movement => movement.Type == LoyaltyMovementType.MembershipAnnulmentWithdrawal));
    await AssertReconciledAsync();
  }

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task LinkAndAnnulment_InEitherOrder_NeverLeavePointsOnAnAnnulledMembership(bool linkFirst)
  {
    if (!_postgres.IsAvailable) return;
    var seed = await SeedAsync();
    Func<Svc, Task> link = services => services.Memberships.LinkIncomeAsync(seed.AdminId, seed.MembershipId, new MembershipIncomeLinkRequest(seed.IncomeId));
    Func<Svc, Task> annul = services => services.Memberships.AnnulAsync(seed.AdminId, seed.MembershipId, new MembershipAnnulRequest("Pago devuelto"));

    var (first, second) = linkFirst
        ? await RaceAsync(LockOfOneMembership, link, annul)
        : await RaceAsync(LockOfOneMembership, annul, link);

    Assert.Null(first);
    Assert.Null(second);
    await using var verify = CreateContext(null);
    var membership = await verify.CustomerMemberships.AsNoTracking().SingleAsync(candidate => candidate.Id == seed.MembershipId);
    Assert.Equal(MembershipStatus.Annulled, membership.Status);
    Assert.Equal(
        linkFirst ? MembershipLoyaltyDecision.Accrued : MembershipLoyaltyDecision.NotAccruedAnnulled,
        membership.LoyaltyDecision);
    Assert.Equal(0, await verify.LoyaltyLots.Where(lot => lot.SourceId == seed.MembershipId).SumAsync(lot => lot.RemainingPoints));
    Assert.Equal(linkFirst ? 1 : 0, await verify.LoyaltyMovements.CountAsync(movement => movement.Type == LoyaltyMovementType.MembershipAnnulmentWithdrawal));
    await AssertReconciledAsync();
  }

  [Fact]
  public async Task ConcurrentAdjustmentsWithTheSameKey_RecordOne()
  {
    if (!_postgres.IsAvailable) return;
    var seed = await SeedAsync();
    var request = new LoyaltyAdjustmentRequest("Credit", 25, "Bono por reclamo", Guid.NewGuid());

    var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
    {
      await using var context = CreateContext(null);
      return (await Services(context).Wallet.AdjustAsync(seed.AdminId, seed.SourceId, request)).AvailablePoints;
    }));

    Assert.All(results, points => Assert.Equal(25, points));
    await using var verify = CreateContext(null);
    Assert.Equal((1, 1), (await verify.LoyaltyAdjustments.CountAsync(), await verify.LoyaltyLots.CountAsync()));
    await AssertReconciledAsync();
  }

  // ── Harness ────────────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Runs <paramref name="holder"/> until the command matching <paramref name="pauseAfter"/> has run (its locks held),
  /// starts <paramref name="contender"/>, waits until PostgreSQL shows it blocked on a lock, then lets both finish.
  /// Returns the exception of each, or null.
  /// </summary>
  private async Task<(Exception? Holder, Exception? Contender)> RaceAsync(
      string pauseAfter, Func<Svc, Task> holder, Func<Svc, Task> contender)
  {
    var gate = new LockGate(pauseAfter);
    await using var holderContext = CreateContext(gate);
    await using var contenderContext = CreateContext(null);

    var holding = Task.Run(() => Capture(() => holder(Services(holderContext))));
    await gate.Held.WaitAsync(Timeout);
    var contending = Task.Run(() => Capture(() => contender(Services(contenderContext))));
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

  private async Task AssertReconciledAsync()
  {
    await using var context = CreateContext(null);
    var movements = await context.LoyaltyMovements.AsNoTracking().ToListAsync();
    foreach (var lot in await context.LoyaltyLots.AsNoTracking().ToListAsync())
    {
      var own = movements.Where(movement => movement.LotId == lot.Id).ToList();
      Assert.Equal(lot.InitialPoints, Assert.Single(own, movement => LoyaltyMovement.LotSign(movement.Type) > 0).Points);
      Assert.Equal(lot.RemainingPoints, own.Sum(movement => LoyaltyMovement.LotSign(movement.Type) * movement.Points));
    }
  }

  private async Task<LoyaltyLot> SingleLotAsync(Guid membershipId)
  {
    await using var context = CreateContext(null);
    return await context.LoyaltyLots.AsNoTracking().SingleAsync(lot => lot.SourceId == membershipId);
  }

  private sealed record Seed(Guid AdminId, Guid SourceId, Guid TargetId, Guid MembershipId, Guid IncomeId);

  /// <summary>Program enabled; a 40-point membership sold to the source profile and an income to pay it.</summary>
  private async Task<Seed> SeedAsync(bool linked = false)
  {
    await using var context = CreateContext(null);
    await context.Database.EnsureDeletedAsync();
    await context.Database.MigrateAsync();

    var admin = new User("Admin Principal", "admin@example.com", "hash", DateTime.UtcNow);
    var haircut = new Service("Corte", 25000, DateTime.UtcNow, 30);
    context.Users.Add(admin);
    context.Services.Add(haircut);
    var settings = await context.LoyaltySettings.SingleAsync();
    settings.Update(true, 365, admin.Id, DateTime.UtcNow);
    await context.SaveChangesAsync();

    var services = Services(context, admin.Id);
    var source = await services.Customers.CreateAsync(admin.Id, new CustomerCreateRequest("Carla Origen", "3001234567", "origen@example.com"));
    var target = await services.Customers.CreateAsync(admin.Id, new CustomerCreateRequest("Carla Destino", "3001234567", "destino@example.com"));
    var plan = await services.Plans.CreateAsync(new MembershipPlanUpsertRequest(
        "Oro", null, 250000, "Pool", 10, 90, Terms, null, [new(haircut.Id, null)], [], 40));
    var today = DateOnly.FromDateTime(BogotaClock.ToLocal(DateTime.UtcNow));
    var membership = await services.Memberships.SellAsync(admin.Id, source.Id, new MembershipSaleRequest(plan.Id, today, ActivateNow: true));
    var income = IncomeEntry.CreateMembershipSale(250000, today, admin.Id, DateTime.UtcNow);
    context.IncomeEntries.Add(income);
    await context.SaveChangesAsync();

    if (linked)
    {
      await services.Memberships.LinkIncomeAsync(admin.Id, membership.Id, new MembershipIncomeLinkRequest(income.Id));
    }

    return new Seed(admin.Id, source.Id, target.Id, membership.Id, income.Id);
  }

  private sealed record Svc(CustomerManagementService Customers, MembershipSalesService Memberships, MembershipPlanManagementService Plans, LoyaltyWalletService Wallet);

  private static Svc Services(AppDbContext context, Guid? currentUserId = null)
      => new(
          new CustomerManagementService(context, TimeProvider.System),
          new MembershipSalesService(context, TimeProvider.System),
          new MembershipPlanManagementService(context, TimeProvider.System, new FixedCurrentUserAccessor(currentUserId)),
          new LoyaltyWalletService(context, TimeProvider.System));

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

  private sealed class FixedCurrentUserAccessor(Guid? userId) : ICurrentUserAccessor
  {
    public Guid? UserId { get; } = userId;
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
