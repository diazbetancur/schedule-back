using System.Data.Common;
using Barbershop.Application.Auth;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Application.ServiceRecords;
using Barbershop.Domain.Common;
using Barbershop.Domain.ServiceRecords;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Customers;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.ServiceRecords;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Barbershop.Tests.Features.ServiceRecords;

/// <summary>
/// Approving a service record as a membership consumption while a merge moves that membership, forced deterministically
/// on real PostgreSQL in both orders: the first operation is held right after taking its lock, the second is observed
/// waiting in pg_stat_activity, then the first is released. The approval revalidates owner and profile after locking
/// the membership: it either happens entirely before the merge or answers 409 without approving or consuming anything.
/// Either way the membership is consumed at most once and its balance reconciles with its consumptions.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "ServiceRecordsPostgres")]
public sealed class MembershipConsumptionMergeRaceTests
{
  private const string MergeLocksMemberships = "customer_memberships WHERE \"CustomerId\" =";
  private const string ApprovalLocksTheMembership = "customer_memberships WHERE \"Id\" =";
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

  private readonly PostgresContainerFixture _postgres;

  public MembershipConsumptionMergeRaceTests(PostgresContainerFixture postgres) => _postgres = postgres;

  [Fact]
  public async Task MergeFirst_TheWaitingApprovalIsAConflict_WithoutApprovalOrConsumption_AndARetryOnTheSurvivorConsumesOnce()
  {
    if (!_postgres.IsAvailable) return; // Only with PostgresContainerFixture.AllowSkipVariable set on purpose.
    var seed = await SeedAsync();
    var token = await PreviewTokenAsync(seed);

    var (merge, approval) = await RaceAsync(
        MergeLocksMemberships,
        context => Customers(context).MergeAsync(seed.AdminId, seed.TargetId, new CustomerMergeRequest(seed.SourceId, token)),
        context => Records(context).ApproveAsync(seed.AdminId, seed.RecordId, Consumption(seed, seed.SourceId)));

    Assert.Null(merge);
    Assert.IsType<ConflictException>(approval);
    await using (var verify = CreateContext(null))
    {
      var record = await verify.ServiceRecords.AsNoTracking().SingleAsync(candidate => candidate.Id == seed.RecordId);
      Assert.Equal((ServiceRecordStatus.Pending, (Guid?)null), (record.Status, record.MembershipConsumptionId));
      Assert.Equal(0, await verify.MembershipConsumptions.CountAsync());
      Assert.Equal(seed.TargetId, (await verify.CustomerMemberships.AsNoTracking().SingleAsync()).CustomerId);
    }

    // The reviewer reloads and confirms with the surviving profile: one consumption, and the balance reconciles.
    await using (var retry = CreateContext(null))
    {
      await Records(retry).ApproveAsync(seed.AdminId, seed.RecordId, Consumption(seed, seed.TargetId));
    }

    await AssertConsumedOnceAsync(seed, expectedCustomerId: seed.TargetId);
  }

  [Fact]
  public async Task ApprovalFirst_TheWaitingMergeMovesTheMembershipAfterwards_AndItIsConsumedOnce()
  {
    if (!_postgres.IsAvailable) return;
    var seed = await SeedAsync();
    var token = await PreviewTokenAsync(seed);

    var (approval, merge) = await RaceAsync(
        ApprovalLocksTheMembership,
        context => Records(context).ApproveAsync(seed.AdminId, seed.RecordId, Consumption(seed, seed.SourceId)),
        context => Customers(context).MergeAsync(seed.AdminId, seed.TargetId, new CustomerMergeRequest(seed.SourceId, token)));

    Assert.Null(approval);
    Assert.Null(merge);
    await using (var verify = CreateContext(null))
    {
      Assert.Equal(seed.TargetId, (await verify.Customers.AsNoTracking().SingleAsync(customer => customer.Id == seed.SourceId)).MergedIntoCustomerId);
    }

    // Same outcome as approving and then merging: the record keeps the profile it was confirmed for.
    await AssertConsumedOnceAsync(seed, expectedCustomerId: seed.SourceId);
  }

  // ── Helpers ────────────────────────────────────────────────────────────────────────────────

  private async Task AssertConsumedOnceAsync(Seed seed, Guid expectedCustomerId)
  {
    await using var verify = CreateContext(null);
    var record = await verify.ServiceRecords.AsNoTracking().SingleAsync(candidate => candidate.Id == seed.RecordId);
    Assert.Equal((ServiceRecordStatus.Approved, expectedCustomerId), (record.Status, record.ApprovedCustomerId!.Value));
    var consumption = await verify.MembershipConsumptions.AsNoTracking().SingleAsync();
    Assert.Equal((record.MembershipConsumptionId!.Value, seed.MembershipId), (consumption.Id, consumption.CustomerMembershipId));
    Assert.Equal(seed.TargetId, (await verify.CustomerMemberships.AsNoTracking().SingleAsync()).CustomerId);

    var detail = await new MembershipSalesService(verify, TimeProvider.System).GetByIdAsync(seed.MembershipId);
    var balance = Assert.Single(detail.Allowances);
    Assert.Equal((10, 1, 9), (balance.GrantedQuantity, balance.ConsumedQuantity, balance.AvailableQuantity));
  }

  private async Task<(Exception? Holder, Exception? Contender)> RaceAsync(
      string pauseAfter, Func<AppDbContext, Task> holder, Func<AppDbContext, Task> contender)
  {
    var gate = new LockGate(pauseAfter);
    await using var holderContext = CreateContext(gate);
    await using var contenderContext = CreateContext(null);

    var holding = Task.Run(() => Capture(() => holder(holderContext)));
    await gate.Held.WaitAsync(Timeout);
    var contending = Task.Run(() => Capture(() => contender(contenderContext)));
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

  private sealed record Seed(Guid AdminId, Guid SourceId, Guid TargetId, Guid MembershipId, Guid RecordId, Guid ServiceId, Guid StaffId, DateOnly Today);

  /// <summary>A pool membership of 10 haircuts sold to the source profile and a pending haircut record of that profile.</summary>
  private async Task<Seed> SeedAsync()
  {
    await using var context = CreateContext(null);
    await context.Database.EnsureDeletedAsync();
    await context.Database.MigrateAsync();

    var today = DateOnly.FromDateTime(BogotaClock.ToLocal(DateTime.UtcNow));
    var admin = new User("Admin Principal", "admin@example.com", "hash", DateTime.UtcNow);
    var barberUser = new User("Alex Barbero", "alex@example.com", "hash", DateTime.UtcNow);
    var haircut = new Service("Corte", 25000, DateTime.UtcNow, 30);
    var staff = new StaffProfile(barberUser.Id, "Alex", 30, DateTime.UtcNow);
    context.Users.AddRange(admin, barberUser);
    context.Services.Add(haircut);
    context.StaffProfiles.Add(staff);
    await context.SaveChangesAsync();

    var customers = Customers(context);
    var source = await customers.CreateAsync(admin.Id, new CustomerCreateRequest("Carla Origen", "3001234567", "origen@example.com"));
    var target = await customers.CreateAsync(admin.Id, new CustomerCreateRequest("Carla Destino", "3001234567", "destino@example.com"));
    var plan = await new MembershipPlanManagementService(context, TimeProvider.System, new FixedUser(admin.Id)).CreateAsync(
        new MembershipPlanUpsertRequest("Oro", null, 250000, "Pool", 10, 90, "La membresía es personal e intransferible.", null, [new(haircut.Id, null)], [], 0));
    var membership = await new MembershipSalesService(context, TimeProvider.System)
        .SellAsync(admin.Id, source.Id, new MembershipSaleRequest(plan.Id, today, ActivateNow: true));
    var record = new ServiceRecord(Guid.NewGuid(), 0, null, staff.Id, haircut.Id, "Corte", today, source.Id, "Carla Origen", null, barberUser.Id, DateTime.UtcNow);
    context.ServiceRecords.Add(record);
    await context.SaveChangesAsync();

    return new Seed(admin.Id, source.Id, target.Id, membership.Id, record.Id, haircut.Id, staff.Id, today);
  }

  private async Task<string> PreviewTokenAsync(Seed seed)
  {
    await using var context = CreateContext(null);
    return (await Customers(context).GetMergePreviewAsync(seed.TargetId, seed.SourceId)).PreviewToken;
  }

  /// <summary>The version is read fresh: a stale one would be refused for another reason.</summary>
  private ServiceRecordApproveRequest Consumption(Seed seed, Guid customerId)
  {
    using var context = CreateContext(null);
    var version = context.ServiceRecords.AsNoTracking().Single(candidate => candidate.Id == seed.RecordId).Version;
    return new ServiceRecordApproveRequest(version, "MembershipConsumption", seed.ServiceId, seed.StaffId, customerId, seed.Today, null, seed.MembershipId, false, null);
  }

  private static CustomerManagementService Customers(AppDbContext context) => new(context, TimeProvider.System);

  private static ServiceRecordsService Records(AppDbContext context)
      => new(context, new MembershipLedgerService(context, new MembershipSalesService(context, TimeProvider.System), TimeProvider.System), TimeProvider.System);

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

  private sealed class FixedUser(Guid? userId) : ICurrentUserAccessor
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
