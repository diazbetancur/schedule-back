using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Barbershop.Tests.Features.Loyalty;

/// <summary>
/// AddLoyaltyRedemptions over an existing wallet (seeded with SQL): lots, movements and settings keep their data,
/// lots start with nothing reserved, the settings take the approved initial deadlines (30 and 30 days), the new
/// constraints hold, and rolling back drops only what the migration added.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "LoyaltyRedemptionMigration")]
public sealed class LoyaltyRedemptionMigrationTests
{
  private const string MigrationBeforeRedemptions = "20261104131450_AddLoyaltyWallet";
  private static readonly string[] NewTables = ["loyalty_rewards", "loyalty_reward_changes", "loyalty_redemptions", "loyalty_redemption_allocations"];

  private readonly PostgresContainerFixture _postgres;

  public LoyaltyRedemptionMigrationTests(PostgresContainerFixture postgres) => _postgres = postgres;

  [Fact]
  public async Task AnExistingWallet_IsKept_WithNothingReserved_AndTheMigrationRollsBackCleanly()
  {
    if (!_postgres.IsAvailable) return; // Only with PostgresContainerFixture.AllowSkipVariable set on purpose.

    await using var db = CreateContext();
    await db.Database.EnsureDeletedAsync();
    await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeRedemptions);

    var userId = Guid.NewGuid();
    var customerId = Guid.NewGuid();
    var lotId = Guid.NewGuid();
    var now = DateTime.UtcNow;
    var today = DateOnly.FromDateTime(now);
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.users ("Id", "FullName", "Email", "NormalizedEmail", "PasswordHash", "IsActive", "CreatedAt", "FailedLoginCount")
        VALUES ({userId}, {"Admin"}, {"admin-red@example.com"}, {"ADMIN-RED@EXAMPLE.COM"}, {"hash"}, {true}, {now}, {0})
        """);
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.customers ("Id", "FullName", "Email", "NormalizedEmail", "Phone", "NormalizedPhone", "CreatedByUserId", "CreatedAt")
        VALUES ({customerId}, {"Carla"}, {"carla-red@example.com"}, {"CARLA-RED@EXAMPLE.COM"}, {"3001234567"}, {"3001234567"}, {userId}, {now})
        """);
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.loyalty_lots ("Id", "CustomerId", "SourceType", "SourceId", "InitialPoints", "RemainingPoints", "AccruedAt", "AccruedOn", "ValidityDays", "ExpiresOn", "CreatedAt")
        VALUES ({lotId}, {customerId}, {"Adjustment"}, {Guid.NewGuid()}, {50}, {50}, {now}, {today}, {365}, {today.AddDays(364)}, {now})
        """);
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.loyalty_movements ("Id", "CustomerId", "Type", "Points", "LotId", "PerformedByUserId", "PerformedAt")
        VALUES ({Guid.NewGuid()}, {customerId}, {"AdjustmentCredit"}, {50}, {lotId}, {userId}, {now})
        """);
    string[] lotColumns = ["ReservedPoints"];
    string[] movementColumns = ["RedemptionId"];
    string[] settingsColumns = ["RedemptionApprovalDays", "RedemptionDeliveryDays"];
    var lots = await SnapshotAsync(db, "loyalty_lots", lotColumns);
    var movements = await SnapshotAsync(db, "loyalty_movements", movementColumns);
    var settings = await SnapshotAsync(db, "loyalty_settings", settingsColumns);

    await db.GetService<IMigrator>().MigrateAsync();

    Assert.Equal(lots, await SnapshotAsync(db, "loyalty_lots", lotColumns));
    Assert.Equal(movements, await SnapshotAsync(db, "loyalty_movements", movementColumns));
    Assert.Equal(settings, await SnapshotAsync(db, "loyalty_settings", settingsColumns));
    Assert.Equal(0, (await db.LoyaltyLots.AsNoTracking().SingleAsync()).ReservedPoints);
    Assert.Null((await db.LoyaltyMovements.AsNoTracking().SingleAsync()).RedemptionId);
    var migratedSettings = await db.LoyaltySettings.AsNoTracking().SingleAsync();
    Assert.Equal((30, 30), (migratedSettings.RedemptionApprovalDays, migratedSettings.RedemptionDeliveryDays));
    foreach (var table in NewTables)
    {
      Assert.Equal(0, await db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM public.{table}").SingleAsync());
    }

    // Free plus reserved never exceed what the lot opened with; a service reward names its service.
    var overReserved = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
        $"UPDATE public.loyalty_lots SET \"ReservedPoints\" = {1} WHERE \"Id\" = {lotId}"));
    Assert.Equal(PostgresErrorCodes.CheckViolation, overReserved.SqlState);
    var serviceRewardWithoutService = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.loyalty_rewards ("Id", "Name", "Kind", "ServiceId", "PointsCost", "IsActive", "CreatedByUserId", "CreatedAt")
        VALUES ({Guid.NewGuid()}, {"Corte gratis"}, {"Service"}, {null}, {40}, {true}, {userId}, {now})
        """));
    Assert.Equal(PostgresErrorCodes.CheckViolation, serviceRewardWithoutService.SqlState);
    var badDeadlines = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
        $"UPDATE public.loyalty_settings SET \"RedemptionApprovalDays\" = {0}"));
    Assert.Equal(PostgresErrorCodes.CheckViolation, badDeadlines.SqlState);

    await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeRedemptions);
    Assert.Equal(lots, await SnapshotAsync(db, "loyalty_lots", []));
    Assert.Equal(movements, await SnapshotAsync(db, "loyalty_movements", []));
    Assert.Equal(settings, await SnapshotAsync(db, "loyalty_settings", []));
    Assert.Equal(0, await db.Database.SqlQuery<int>($"""
        SELECT count(*)::int AS "Value" FROM information_schema.tables
        WHERE table_schema = 'public' AND table_name IN ('loyalty_rewards', 'loyalty_reward_changes', 'loyalty_redemptions', 'loyalty_redemption_allocations')
        """).SingleAsync());
  }

  private AppDbContext CreateContext()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(_postgres.ConnectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
        .Options;
    return new AppDbContext(options);
  }

  private static async Task<List<string>> SnapshotAsync(AppDbContext db, string table, string[] exclude)
  {
    var connection = (NpgsqlConnection)db.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open)
    {
      await connection.OpenAsync();
    }

    await using var command = new NpgsqlCommand($"SELECT * FROM public.{table} ORDER BY \"Id\"", connection);
    await using var reader = await command.ExecuteReaderAsync();
    var rows = new List<string>();
    while (await reader.ReadAsync())
    {
      rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
          .Where(index => reader.GetName(index) != "xmin" && !exclude.Contains(reader.GetName(index)))
          .Select(index => $"{reader.GetName(index)}={(reader.IsDBNull(index) ? "null" : Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture))}")));
    }

    return rows;
  }
}
