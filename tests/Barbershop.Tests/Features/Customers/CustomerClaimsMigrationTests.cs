using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Customers;

/// <summary>AddCustomerClaims over existing customers: nothing changes in them, and the new constraints hold.</summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "CustomerClaimsMigration")]
public sealed class CustomerClaimsMigrationTests
{
  private const string MigrationBeforeClaims = "20261103190734_AddServiceRecords";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public CustomerClaimsMigrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task ExistingCustomersAndLinks_AreUntouched_AndOnlyOnePendingClaimPerAccountIsAllowed()
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return;
    }

    await using var db = CreateContext();
    await db.Database.EnsureDeletedAsync();
    await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeClaims);

    var userId = Guid.NewGuid();
    var now = DateTime.UtcNow;
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.users ("Id", "FullName", "Email", "NormalizedEmail", "PasswordHash", "IsActive", "CreatedAt", "FailedLoginCount")
        VALUES ({userId}, {"Carla"}, {"carla-mig@example.com"}, {"CARLA-MIG@EXAMPLE.COM"}, {"hash"}, {true}, {now}, {0})
        """);
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.customers ("Id", "FullName", "Email", "NormalizedEmail", "Phone", "NormalizedPhone", "UserId", "LinkedAt", "LinkedByUserId", "CreatedByUserId", "CreatedAt")
        VALUES ({Guid.NewGuid()}, {"Carla"}, {"carla-mig@example.com"}, {"CARLA-MIG@EXAMPLE.COM"}, {"3001234567"}, {"3001234567"}, {userId}, {now}, {userId}, {userId}, {now}),
               ({Guid.NewGuid()}, {"Sin cuenta"}, {"sin@example.com"}, {"SIN@EXAMPLE.COM"}, {"3007654321"}, {"3007654321"}, {null}, {null}, {null}, {userId}, {now})
        """);
    var before = await SnapshotAsync(db);

    await db.GetService<IMigrator>().MigrateAsync();

    Assert.Equal(before, await SnapshotAsync(db));
    Assert.Empty(await db.CustomerClaims.ToListAsync());

    await InsertClaimAsync(db, userId, "Pending");
    var duplicate = await Assert.ThrowsAsync<PostgresException>(() => InsertClaimAsync(db, userId, "Pending"));
    Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    var malformed = await Assert.ThrowsAsync<PostgresException>(() => InsertClaimAsync(db, userId, "Approved"));
    Assert.Equal(PostgresErrorCodes.CheckViolation, malformed.SqlState);

    // Rolling back drops only the new table.
    await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeClaims);
    Assert.Equal(before, await SnapshotAsync(db));
    Assert.Equal(0, await db.Database.SqlQuery<int>(
        $"SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'customer_claims'").SingleAsync());
  }

  private AppDbContext CreateContext()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(_postgres.ConnectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
        .Options;
    return new AppDbContext(options);
  }

  /// <summary>An approved claim needs a profile and a decision; inserting it bare must violate the status shape.</summary>
  private static Task<int> InsertClaimAsync(AppDbContext db, Guid userId, string status)
      => db.Database.ExecuteSqlInterpolatedAsync($"""
          INSERT INTO public.customer_claims ("Id", "UserId", "IdempotencyKey", "FullName", "Phone", "NormalizedPhone", "Email", "NormalizedEmail", "SubmittedAt", "Status")
          VALUES ({Guid.NewGuid()}, {userId}, {Guid.NewGuid()}, {"Carla"}, {"3001234567"}, {"3001234567"}, {"carla-mig@example.com"}, {"CARLA-MIG@EXAMPLE.COM"}, {DateTime.UtcNow}, {status})
          """);

  private static async Task<List<string>> SnapshotAsync(AppDbContext db)
  {
    var connection = (NpgsqlConnection)db.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open)
    {
      await connection.OpenAsync();
    }

    await using var command = new NpgsqlCommand("SELECT * FROM public.customers ORDER BY \"Id\"", connection);
    await using var reader = await command.ExecuteReaderAsync();
    var rows = new List<string>();
    while (await reader.ReadAsync())
    {
      rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
          .Where(index => reader.GetName(index) != "xmin")
          .Select(index => $"{reader.GetName(index)}={(reader.IsDBNull(index) ? "null" : Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture))}")));
    }

    return rows;
  }
}
