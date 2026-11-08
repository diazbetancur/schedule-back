using Barbershop.Domain.Common;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Loyalty;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Barbershop.Tests.Features.Loyalty;

/// <summary>
/// The expiration job as the API hosts it (LoyaltyExpirationBackgroundWorker), on a disposable PostgreSQL, with a
/// short startup delay and a 1-second interval instead of 6 hours: it records each expired lot exactly once and
/// later runs (and a direct run) add nothing; usable lots are untouched.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "LoyaltyExpirationWorkerPostgres")]
public sealed class LoyaltyExpirationWorkerPostgresTests
{
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

  private readonly PostgresContainerFixture _postgres;

  public LoyaltyExpirationWorkerPostgresTests(PostgresContainerFixture postgres) => _postgres = postgres;

  [Fact]
  public async Task TheHostedJob_RecordsEachExpiredLotOnce_AndRepeatedRunsAddNothing()
  {
    if (!_postgres.IsAvailable) return; // Only with PostgresContainerFixture.AllowSkipVariable set on purpose.

    // The reset drops the database (WITH FORCE, killing open connections), so it runs in a host where the job is
    // disabled; the host with the job starts afterwards on the already migrated database and never sees a drop.
    using (var reset = new IntegrationTestFactory(_postgres.ConnectionString))
    {
      await reset.ResetDatabaseAsync();
    }

    using var factory = new IntegrationTestFactory(_postgres.ConnectionString);
    // The factory disables the job; enable it for this host only, before the host is built (restored below).
    var settings = new Dictionary<string, string>
    {
      ["LoyaltyExpirationWorker__Enabled"] = "true",
      ["LoyaltyExpirationWorker__StartupDelaySeconds"] = "5",
      ["LoyaltyExpirationWorker__IntervalSeconds"] = "1",
    };
    var previous = settings.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
    foreach (var (key, value) in settings)
    {
      Environment.SetEnvironmentVariable(key, value);
    }

    try
    {
      // Building the host starts the job; its first run comes 5 s later.
      var (expiredLotId, usableLotId) = await SeedLotsAsync(factory);

      // First run: the expired lot gets its expiration.
      var deadline = DateTime.UtcNow + Timeout;
      while (await CountExpirationsAsync(factory) == 0)
      {
        Assert.True(DateTime.UtcNow < deadline, "The hosted expiration job never ran.");
        await Task.Delay(250);
      }

      // Several more runs (1 s apart) and a direct run add nothing.
      await Task.Delay(TimeSpan.FromSeconds(4));
      using (var scope = factory.Services.CreateScope())
      {
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<LoyaltyWalletService>().ExpireDueLotsAsync(CancellationToken.None));
      }

      using (var scope = factory.Services.CreateScope())
      {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var expiration = await db.LoyaltyMovements.AsNoTracking().SingleAsync(movement => movement.Type == LoyaltyMovementType.Expiration);
        Assert.Equal((expiredLotId, 12, null), (expiration.LotId!.Value, expiration.Points, expiration.PerformedByUserId));
        var lots = await db.LoyaltyLots.AsNoTracking().ToDictionaryAsync(lot => lot.Id);
        Assert.Equal((0, 20), (lots[expiredLotId].RemainingPoints, lots[usableLotId].RemainingPoints));

        var movements = await db.LoyaltyMovements.AsNoTracking().ToListAsync();
        foreach (var lot in lots.Values)
        {
          var own = movements.Where(movement => movement.LotId == lot.Id).ToList();
          Assert.Equal(lot.InitialPoints, Assert.Single(own, movement => LoyaltyMovement.LotSign(movement.Type) > 0).Points);
          Assert.Equal(lot.RemainingPoints, own.Sum(movement => LoyaltyMovement.LotSign(movement.Type) * movement.Points));
        }
      }
    }
    finally
    {
      foreach (var (key, value) in previous)
      {
        Environment.SetEnvironmentVariable(key, value);
      }
    }
  }

  private static async Task<int> CountExpirationsAsync(IntegrationTestFactory factory)
  {
    using var scope = factory.Services.CreateScope();
    return await scope.ServiceProvider.GetRequiredService<AppDbContext>().LoyaltyMovements
        .CountAsync(movement => movement.Type == LoyaltyMovementType.Expiration);
  }

  /// <summary>One lot whose last valid day was yesterday (Bogotá) and one still valid, each with its opening movement.</summary>
  private static async Task<(Guid ExpiredLotId, Guid UsableLotId)> SeedLotsAsync(IntegrationTestFactory factory)
  {
    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var now = DateTime.UtcNow;
    var today = DateOnly.FromDateTime(BogotaClock.ToLocal(now));

    var admin = new User("Admin", "admin-worker@example.com", "hash", now);
    db.Users.Add(admin);
    var customer = new CustomerProfile("Carla", "3001234567", "carla-worker@example.com", admin.Id, now);
    db.Customers.Add(customer);

    var expired = LoyaltyLot.Accrue(customer.Id, LoyaltySourceType.Adjustment, Guid.NewGuid(), 12, now.AddDays(-2), today.AddDays(-2), validityDays: 2);
    var usable = LoyaltyLot.Accrue(customer.Id, LoyaltySourceType.Adjustment, Guid.NewGuid(), 20, now, today, validityDays: 365);
    db.LoyaltyLots.AddRange(expired, usable);
    db.LoyaltyMovements.AddRange(
        new LoyaltyMovement(customer.Id, LoyaltyMovementType.AdjustmentCredit, 12, now.AddDays(-2), lotId: expired.Id, performedByUserId: admin.Id),
        new LoyaltyMovement(customer.Id, LoyaltyMovementType.AdjustmentCredit, 20, now, lotId: usable.Id, performedByUserId: admin.Id));
    await db.SaveChangesAsync();
    return (expired.Id, usable.Id);
  }
}
