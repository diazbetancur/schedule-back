using Barbershop.Domain.Loyalty;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Loyalty;

/// <summary>
/// Regression for the AddLoyaltySettingsAndCatalogPoints migration over a database that already has services,
/// membership types, sold memberships and income written by the previous schema.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "LoyaltyMigrationRegression")]
public sealed class LoyaltyMigrationRegressionTests
{
  private const string MigrationBeforeLoyalty = "20261029123442_AddMembershipGiftDeliveries";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public LoyaltyMigrationRegressionTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task ExistingCatalog_KeepsEveryValue_GetsZeroPoints_AndTheProgramStartsDisabled()
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return;
    }

    await using var db = CreateContext();
    await db.Database.EnsureDeletedAsync();
    await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeLoyalty);

    var history = await SeedHistoryWithTheOldSchemaAsync(db);
    var servicesBefore = await SnapshotAsync(db, "SELECT \"Id\", \"Name\", \"BasePrice\", \"BusinessPercentage\", \"IsActive\", \"IsDeleted\" FROM public.services ORDER BY \"Id\"");
    var plansBefore = await SnapshotAsync(db, "SELECT \"Id\", \"Name\", \"Price\", \"Mode\", \"PoolQuantity\", \"DefaultDurationDays\", \"IsActive\", \"IsDeleted\" FROM public.membership_plans ORDER BY \"Id\"");
    var incomeBefore = await SnapshotAsync(db, "SELECT * FROM public.income_entries ORDER BY \"Id\"");
    // The columns that exist now: later migrations may add new ones (stage 4 adds the loyalty decision), which
    // must not count as changes to the historical rows.
    var membershipColumns = string.Join(", ", (await db.Database.SqlQuery<string>($"""
        SELECT column_name AS "Value" FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'customer_memberships' ORDER BY ordinal_position
        """).ToListAsync()).Select(column => $"\"{column}\""));
    var membershipsSql = $"SELECT {membershipColumns} FROM public.customer_memberships ORDER BY \"Id\"";
    var membershipsBefore = await SnapshotAsync(db, membershipsSql);

    await db.GetService<IMigrator>().MigrateAsync();

    // Historical rows are untouched.
    Assert.Equal(servicesBefore, await SnapshotAsync(db, "SELECT \"Id\", \"Name\", \"BasePrice\", \"BusinessPercentage\", \"IsActive\", \"IsDeleted\" FROM public.services ORDER BY \"Id\""));
    Assert.Equal(plansBefore, await SnapshotAsync(db, "SELECT \"Id\", \"Name\", \"Price\", \"Mode\", \"PoolQuantity\", \"DefaultDurationDays\", \"IsActive\", \"IsDeleted\" FROM public.membership_plans ORDER BY \"Id\""));
    Assert.Equal(incomeBefore, await SnapshotAsync(db, "SELECT * FROM public.income_entries ORDER BY \"Id\""));
    Assert.Equal(membershipsBefore, await SnapshotAsync(db, membershipsSql));

    // Existing services and membership types, deleted ones included, grant no points until configured.
    var services = await db.Services.AsNoTracking().ToListAsync();
    Assert.Equal(history.ServiceIds.Order(), services.Select(service => service.Id).Order());
    Assert.All(services, service => Assert.Equal(0, service.LoyaltyPoints));
    var plans = await db.MembershipPlans.AsNoTracking().ToListAsync();
    Assert.Equal(history.PlanIds.Order(), plans.Select(plan => plan.Id).Order());
    Assert.All(plans, plan => Assert.Equal(0, plan.LoyaltyPoints));

    var settings = await db.LoyaltySettings.AsNoTracking().SingleAsync();
    Assert.Equal(LoyaltySettings.SingletonId, settings.Id);
    Assert.False(settings.IsEnabled);
    Assert.Equal(LoyaltySettings.DefaultPointsValidityDays, settings.PointsValidityDays);
    Assert.NotEqual(0u, settings.Version);
    Assert.Empty(await db.LoyaltySettingsChanges.ToListAsync());
    Assert.Empty(await db.LoyaltyPointsChanges.ToListAsync());

    // The database itself rejects negative points, a second settings row and an out-of-range validity.
    await AssertCheckViolationAsync(db, "UPDATE public.services SET \"LoyaltyPoints\" = -1");
    await AssertCheckViolationAsync(db, "UPDATE public.membership_plans SET \"LoyaltyPoints\" = -1");
    await AssertCheckViolationAsync(db, "INSERT INTO public.loyalty_settings (\"Id\", \"IsEnabled\", \"PointsValidityDays\", \"CreatedAt\") VALUES (2, false, 30, now())");
    await AssertCheckViolationAsync(db, "UPDATE public.loyalty_settings SET \"PointsValidityDays\" = 0");
  }

  [Fact]
  public async Task TheMigration_CanBeRolledBackAndReapplied_OnAnEmptyLoyaltyState()
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return;
    }

    await using var db = CreateContext();
    await db.Database.EnsureDeletedAsync();
    await db.Database.MigrateAsync();

    await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeLoyalty);
    Assert.Equal(0, await CountColumnsAsync(db, "services", "LoyaltyPoints"));
    Assert.Equal(0, await CountTablesAsync(db, "loyalty_settings"));

    await db.GetService<IMigrator>().MigrateAsync();
    Assert.Equal(1, await CountColumnsAsync(db, "services", "LoyaltyPoints"));
    Assert.Equal(1, await CountColumnsAsync(db, "membership_plans", "LoyaltyPoints"));
    Assert.Single(await db.LoyaltySettings.AsNoTracking().ToListAsync());
  }

  private AppDbContext CreateContext()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(_postgres.ConnectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
        .Options;
    return new AppDbContext(options);
  }

  /// <summary>Inserts with the pre-migration columns only, as rows written by the old code.</summary>
  private static async Task<(Guid[] ServiceIds, Guid[] PlanIds)> SeedHistoryWithTheOldSchemaAsync(AppDbContext db)
  {
    var now = DateTime.UtcNow;
    var haircut = Guid.NewGuid();
    var beard = Guid.NewGuid();
    var deleted = Guid.NewGuid();
    foreach (var (id, name, price, percentage, isActive, isDeleted) in new[]
    {
      (haircut, "Corte", 25000, 30, true, false),
      (beard, "Barba", 15000, 0, false, false),
      (deleted, "Tinte", 40000, 100, false, true),
    })
    {
      await db.Database.ExecuteSqlInterpolatedAsync($"""
          INSERT INTO public.services ("Id", "Name", "BasePrice", "BusinessPercentage", "IsActive", "IsDeleted", "CreatedAt")
          VALUES ({id}, {name}, {price}, {percentage}, {isActive}, {isDeleted}, {now})
          """);
    }

    var pool = Guid.NewGuid();
    var perService = Guid.NewGuid();
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.membership_plans ("Id", "Name", "Price", "Mode", "PoolQuantity", "DefaultDurationDays", "Terms", "IsActive", "IsDeleted", "CreatedAt")
        VALUES ({pool}, {"Oro"}, {250000}, {"Pool"}, {10}, {90}, {"Términos"}, {true}, {false}, {now}),
               ({perService}, {"Plata"}, {180000}, {"PerService"}, {null}, {60}, {"Términos"}, {true}, {false}, {now})
        """);
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.membership_plan_services ("MembershipPlanId", "ServiceId", "Quantity")
        VALUES ({pool}, {haircut}, {null}), ({perService}, {haircut}, {8}), ({perService}, {beard}, {4})
        """);

    var staffUser = Guid.NewGuid();
    var staff = Guid.NewGuid();
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.users ("Id", "FullName", "Email", "NormalizedEmail", "PasswordHash", "IsActive", "CreatedAt", "FailedLoginCount")
        VALUES ({staffUser}, {"Barbero"}, {"barbero-mig@example.com"}, {"BARBERO-MIG@EXAMPLE.COM"}, {"hash"}, {true}, {now}, {0})
        """);
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.staff_profiles ("Id", "UserId", "DisplayName", "DefaultAppointmentDurationMinutes", "IsActive", "CreatedAt")
        VALUES ({staff}, {staffUser}, {"Barbero"}, {30}, {true}, {now})
        """);
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.income_entries
          ("Id", "Kind", "ServiceId", "ServiceNameSnapshot", "BasePriceSnapshot", "StaffProfileId", "Amount", "IsPromo",
           "OccurredOn", "CreatedByUserId", "IsDeleted", "CreatedAt", "BusinessPercentageSnapshot")
        VALUES
          ({Guid.NewGuid()}, {"Service"}, {haircut}, {"Corte"}, {25000}, {staff}, {25000}, {false}, {new DateOnly(2026, 10, 1)}, {staffUser}, {false}, {now}, {30}),
          ({Guid.NewGuid()}, {"MembershipSale"}, {null}, {null}, {0}, {null}, {250000}, {false}, {new DateOnly(2026, 10, 2)}, {staffUser}, {false}, {now}, {100})
        """);

    var customer = Guid.NewGuid();
    var saleIncome = Guid.NewGuid();
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.customers ("Id", "FullName", "Email", "NormalizedEmail", "Phone", "NormalizedPhone", "CreatedByUserId", "CreatedAt")
        VALUES ({customer}, {"Cliente"}, {"cliente-mig@example.com"}, {"CLIENTE-MIG@EXAMPLE.COM"}, {"3001112233"}, {"3001112233"}, {staffUser}, {now})
        """);
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.income_entries
          ("Id", "Kind", "ServiceId", "ServiceNameSnapshot", "BasePriceSnapshot", "StaffProfileId", "Amount", "IsPromo",
           "OccurredOn", "CreatedByUserId", "IsDeleted", "CreatedAt", "BusinessPercentageSnapshot")
        VALUES ({saleIncome}, {"MembershipSale"}, {null}, {null}, {0}, {null}, {180000}, {false}, {new DateOnly(2026, 10, 3)}, {staffUser}, {false}, {now}, {100})
        """);
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.customer_memberships
          ("Id", "CustomerId", "OriginalCustomerId", "MembershipPlanId", "PlanName", "Mode", "Price", "Terms", "Status",
           "SoldOn", "StartsOn", "EndsOn", "ActivatedAt", "ActivatedByUserId", "IncomeEntryId", "CreatedByUserId", "CreatedAt")
        VALUES ({Guid.NewGuid()}, {customer}, {customer}, {perService}, {"Plata"}, {"PerService"}, {180000}, {"Términos"}, {"Active"},
                {new DateOnly(2026, 10, 3)}, {new DateOnly(2026, 10, 3)}, {new DateOnly(2026, 12, 2)}, {now}, {staffUser}, {saleIncome}, {staffUser}, {now})
        """);

    return ([haircut, beard, deleted], [pool, perService]);
  }

  private static async Task<List<string>> SnapshotAsync(AppDbContext db, string sql)
  {
    var connection = (NpgsqlConnection)db.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open)
    {
      await connection.OpenAsync();
    }

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();
    var rows = new List<string>();
    while (await reader.ReadAsync())
    {
      rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
          .Select(index => $"{reader.GetName(index)}={(reader.IsDBNull(index) ? "null" : Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture))}")));
    }

    return rows;
  }

  private static async Task AssertCheckViolationAsync(AppDbContext db, string sql)
  {
    var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
    Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
  }

  private static Task<int> CountColumnsAsync(AppDbContext db, string table, string column)
      => db.Database.SqlQuery<int>($"""
          SELECT count(*)::int AS "Value" FROM information_schema.columns
          WHERE table_schema = 'public' AND table_name = {table} AND column_name = {column}
          """).SingleAsync();

  private static Task<int> CountTablesAsync(AppDbContext db, string table)
      => db.Database.SqlQuery<int>($"""
          SELECT count(*)::int AS "Value" FROM information_schema.tables WHERE table_schema = 'public' AND table_name = {table}
          """).SingleAsync();
}
