using Barbershop.Domain.Memberships;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Barbershop.Tests.Features.Loyalty;

/// <summary>
/// AddLoyaltyWallet over existing memberships (seeded with SQL, the entity already has the new columns): their data
/// is untouched, they are marked as predating the wallet (no snapshot, no decision), the new constraints hold, and
/// rolling back drops only what the migration added.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "LoyaltyWalletMigration")]
public sealed class LoyaltyWalletMigrationTests
{
  private const string MigrationBeforeWallet = "20261103210414_AddCustomerClaims";

  private readonly PostgresContainerFixture _postgres;

  public LoyaltyWalletMigrationTests(PostgresContainerFixture postgres) => _postgres = postgres;

  [Fact]
  public async Task ExistingMemberships_AreKeptAndMarkedAsPredatingTheWallet_AndTheMigrationRollsBackCleanly()
  {
    if (!_postgres.IsAvailable) return; // Only with PostgresContainerFixture.AllowSkipVariable set on purpose.

    await using var db = CreateContext();
    await db.Database.EnsureDeletedAsync();
    await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeWallet);

    var userId = Guid.NewGuid();
    var customerId = Guid.NewGuid();
    var planId = Guid.NewGuid();
    var membershipId = Guid.NewGuid();
    var now = DateTime.UtcNow;
    var today = DateOnly.FromDateTime(now);
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.users ("Id", "FullName", "Email", "NormalizedEmail", "PasswordHash", "IsActive", "CreatedAt", "FailedLoginCount")
        VALUES ({userId}, {"Admin"}, {"admin-mig@example.com"}, {"ADMIN-MIG@EXAMPLE.COM"}, {"hash"}, {true}, {now}, {0})
        """);
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.customers ("Id", "FullName", "Email", "NormalizedEmail", "Phone", "NormalizedPhone", "CreatedByUserId", "CreatedAt")
        VALUES ({customerId}, {"Carla"}, {"carla-mig@example.com"}, {"CARLA-MIG@EXAMPLE.COM"}, {"3001234567"}, {"3001234567"}, {userId}, {now})
        """);
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.membership_plans ("Id", "Name", "Price", "Mode", "PoolQuantity", "DefaultDurationDays", "Terms", "IsActive", "IsDeleted", "LoyaltyPoints", "CreatedAt")
        VALUES ({planId}, {"Oro"}, {250000}, {"Pool"}, {10}, {90}, {"Términos"}, {true}, {false}, {40}, {now})
        """);
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.customer_memberships ("Id", "CustomerId", "OriginalCustomerId", "MembershipPlanId", "PlanName", "Price", "Mode", "Terms",
            "SoldOn", "StartsOn", "EndsOn", "Status", "ActivatedAt", "ActivatedByUserId", "CreatedByUserId", "CreatedAt")
        VALUES ({membershipId}, {customerId}, {customerId}, {planId}, {"Oro"}, {250000}, {"Pool"}, {"Términos"},
            {today}, {today}, {today.AddDays(89)}, {"Active"}, {now}, {userId}, {userId}, {now})
        """);
    var before = await SnapshotAsync(db, "customer_memberships", exclude: ["LoyaltyPointsSnapshot", "LoyaltyDecision", "LoyaltyDecidedAt"]);

    await db.GetService<IMigrator>().MigrateAsync();

    Assert.Equal(before, await SnapshotAsync(db, "customer_memberships", exclude: ["LoyaltyPointsSnapshot", "LoyaltyDecision", "LoyaltyDecidedAt"]));
    var migrated = await db.CustomerMemberships.AsNoTracking().SingleAsync();
    Assert.Equal((null, null, null), (migrated.LoyaltyPointsSnapshot, migrated.LoyaltyDecision, migrated.LoyaltyDecidedAt));
    foreach (var table in new[] { "loyalty_lots", "loyalty_debts", "loyalty_adjustments", "loyalty_movements" })
    {
      Assert.Equal(0, await CountAsync(db, table));
    }

    // An accrued decision needs a positive snapshot; a lot cannot keep more points than it opened with.
    var badDecision = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
        $"UPDATE public.customer_memberships SET \"LoyaltyDecision\" = {nameof(MembershipLoyaltyDecision.Accrued)}, \"LoyaltyDecidedAt\" = {now} WHERE \"Id\" = {membershipId}"));
    Assert.Equal(PostgresErrorCodes.CheckViolation, badDecision.SqlState);
    var badLot = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.loyalty_lots ("Id", "CustomerId", "SourceType", "SourceId", "InitialPoints", "RemainingPoints", "AccruedAt", "AccruedOn", "ValidityDays", "ExpiresOn", "CreatedAt")
        VALUES ({Guid.NewGuid()}, {customerId}, {"Adjustment"}, {Guid.NewGuid()}, {10}, {11}, {now}, {today}, {365}, {today.AddDays(364)}, {now})
        """));
    Assert.Equal(PostgresErrorCodes.CheckViolation, badLot.SqlState);

    // A debt keeps the age of the original one (IncurredAt): it can be older than its row, never newer.
    var badDebt = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.loyalty_debts ("Id", "CustomerId", "Origin", "Amount", "OutstandingPoints", "CreatedAt", "IncurredAt")
        VALUES ({Guid.NewGuid()}, {customerId}, {"PointsReversal"}, {10}, {10}, {now}, {now.AddDays(1)})
        """));
    Assert.Equal(PostgresErrorCodes.CheckViolation, badDebt.SqlState);

    await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeWallet);
    Assert.Equal(before, await SnapshotAsync(db, "customer_memberships", exclude: []));
    Assert.Equal(0, await db.Database.SqlQuery<int>($"""
        SELECT count(*)::int AS "Value" FROM information_schema.tables
        WHERE table_schema = 'public' AND table_name IN ('loyalty_lots', 'loyalty_debts', 'loyalty_adjustments', 'loyalty_movements')
        """).SingleAsync());
  }

  private AppDbContext CreateContext()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(_postgres.ConnectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
        .Options;
    return new AppDbContext(options);
  }

  private static async Task<int> CountAsync(AppDbContext db, string table)
      => await db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM public.{table}").SingleAsync();

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
