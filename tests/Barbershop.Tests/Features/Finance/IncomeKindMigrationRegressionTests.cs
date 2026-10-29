using Barbershop.Application.Finance.Admin;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Finance;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Finance;

/// <summary>
/// Regression for the AddIncomeKind migration: income recorded before membership sales existed must keep
/// every stored value, and reports over those periods must produce exactly the same numbers as the
/// pre-migration report logic (reproduced below as <see cref="LegacyReport"/>).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "IncomeKindMigrationRegression")]
public sealed class IncomeKindMigrationRegressionTests
{
  private const string MigrationBeforeIncomeKind = "20261028211944_AddMembershipPlans";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public IncomeKindMigrationRegressionTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task HistoricalIncome_KeepsItsValues_AndReportsMatchThePreMigrationLogic()
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return;
    }

    await using var db = CreateContext();
    await db.Database.EnsureDeletedAsync();
    await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeIncomeKind);

    var legacyRows = await SeedHistoryWithTheOldSchemaAsync(db);

    // Reports as the pre-migration code computed them, over the same rows.
    var ranges = new[]
    {
      (From: new DateOnly(2026, 6, 1), To: new DateOnly(2026, 6, 30)),   // daily trend
      (From: new DateOnly(2026, 1, 1), To: new DateOnly(2026, 12, 31)),  // monthly trend
      (From: new DateOnly(2026, 3, 15), To: new DateOnly(2026, 3, 15)),  // single day
    };
    var expected = ranges.Select(range => LegacyReport(legacyRows, range.From, range.To)).ToArray();

    await db.GetService<IMigrator>().MigrateAsync();

    // Stored values are untouched and every historical entry became service income.
    var migrated = await db.IncomeEntries.AsNoTracking().ToListAsync();
    Assert.Equal(legacyRows.Count, migrated.Count);
    foreach (var legacy in legacyRows)
    {
      var entry = migrated.Single(candidate => candidate.Id == legacy.Id);
      Assert.Equal(IncomeKind.Service, entry.Kind);
      Assert.Equal(legacy.ServiceId, entry.ServiceId);
      Assert.Equal(legacy.ServiceName, entry.ServiceNameSnapshot);
      Assert.Equal(legacy.BasePrice, entry.BasePriceSnapshot);
      Assert.Equal(legacy.StaffProfileId, entry.StaffProfileId);
      Assert.Equal(legacy.Amount, entry.Amount);
      Assert.Equal(legacy.IsPromo, entry.IsPromo);
      Assert.Equal(legacy.BusinessPercentage, entry.BusinessPercentageSnapshot);
      Assert.Equal(legacy.OccurredOn, entry.OccurredOn);
      Assert.Equal(legacy.IsDeleted, entry.IsDeleted);
      Assert.Equal(legacy.BusinessAmount, entry.BusinessAmount);
      Assert.Equal(legacy.Amount - legacy.BusinessAmount, entry.ProfessionalAmount);
    }

    var reports = new ReportsQueryService(db, TimeProvider.System);
    for (var index = 0; index < ranges.Length; index++)
    {
      var actual = await reports.GetSummaryAsync(ranges[index].From, ranges[index].To);
      var legacy = expected[index];

      Assert.Equal(legacy.TotalIncome, actual.TotalIncome);
      Assert.Equal(legacy.TotalExpenses, actual.TotalExpenses);
      Assert.Equal(legacy.NetProfit, actual.NetProfit);
      Assert.Equal(legacy.IncomeCount, actual.IncomeCount);
      Assert.Equal(legacy.PromoIncome, actual.PromoIncome);
      Assert.Equal(legacy.NormalIncome, actual.NormalIncome);
      Assert.Equal(legacy.BusinessIncome, actual.BusinessIncome);
      Assert.Equal(legacy.ProfessionalIncome, actual.ProfessionalIncome);
      Assert.Equal(0, actual.MembershipSalesIncome);
      Assert.Equal(0, actual.MembershipSalesCount);
      Assert.Equal(
          legacy.ByProfessional.OrderBy(row => row.StaffProfileId),
          actual.ByProfessional
              .Select(row => (row.StaffProfileId, row.IncomeTotal, row.IncomeCount, row.BusinessTotal, row.ProfessionalTotal))
              .OrderBy(row => row.StaffProfileId));
      Assert.Equal(legacy.Trend, actual.Trend.Select(point => (point.Bucket, point.Income, point.Expenses)));
    }
  }

  [Fact]
  public async Task KindConsistencyConstraint_RejectsMembershipSalesWithAServiceProfessionalPromoOrSplit()
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return;
    }

    await using var db = CreateContext();
    await db.Database.EnsureDeletedAsync();
    await db.Database.MigrateAsync();
    var (staffId, serviceId) = await SeedStaffAndServiceAsync(db);

    // Valid membership sale and valid service income are accepted.
    await InsertAsync(db, "MembershipSale", null, null, null, 100, 0, false);
    await InsertAsync(db, "Service", serviceId, staffId, "Corte", 30, 25000, false);

    await Assert.ThrowsAnyAsync<Exception>(() => InsertAsync(db, "MembershipSale", null, staffId, null, 100, 0, false));
    await Assert.ThrowsAnyAsync<Exception>(() => InsertAsync(db, "MembershipSale", serviceId, null, "Corte", 100, 0, false));
    await Assert.ThrowsAnyAsync<Exception>(() => InsertAsync(db, "MembershipSale", null, null, null, 60, 0, false));
    await Assert.ThrowsAnyAsync<Exception>(() => InsertAsync(db, "MembershipSale", null, null, null, 100, 0, true));
    await Assert.ThrowsAnyAsync<Exception>(() => InsertAsync(db, "Service", null, staffId, "Corte", 30, 25000, false));
    await Assert.ThrowsAnyAsync<Exception>(() => InsertAsync(db, "Service", serviceId, null, "Corte", 30, 25000, false));
    await Assert.ThrowsAnyAsync<Exception>(() => InsertAsync(db, "Gift", null, null, null, 100, 0, false));

    Assert.Equal(2, await db.IncomeEntries.CountAsync());
  }

  private AppDbContext CreateContext()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(_postgres.ConnectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
        .Options;
    return new AppDbContext(options);
  }

  private static async Task<(Guid StaffId, Guid ServiceId)> SeedStaffAndServiceAsync(AppDbContext db)
  {
    var user = new User("Barbero", $"barbero-{Guid.NewGuid():N}@example.com", "hash", DateTime.UtcNow);
    var staff = new StaffProfile(user.Id, "Barbero", 30, DateTime.UtcNow);
    var service = new Service("Corte", 25000, DateTime.UtcNow, 30);
    db.Users.Add(user);
    db.StaffProfiles.Add(staff);
    db.Services.Add(service);
    await db.SaveChangesAsync();
    return (staff.Id, service.Id);
  }

  private static Task<int> InsertAsync(
      AppDbContext db, string kind, Guid? serviceId, Guid? staffId, string? serviceName, int percentage, int basePrice, bool isPromo)
      => db.Database.ExecuteSqlInterpolatedAsync($"""
          INSERT INTO public.income_entries
            ("Id", "Kind", "ServiceId", "ServiceNameSnapshot", "BasePriceSnapshot", "StaffProfileId", "Amount", "IsPromo",
             "OccurredOn", "CreatedByUserId", "IsDeleted", "CreatedAt", "BusinessPercentageSnapshot")
          VALUES
            ({Guid.NewGuid()}, {kind}, {serviceId}, {serviceName}, {basePrice}, {staffId}, {10000}, {isPromo},
             {new DateOnly(2026, 6, 1)}, {Guid.NewGuid()}, {false}, {DateTime.UtcNow}, {percentage})
          """);

  /// <summary>Inserts with the pre-migration columns only (no Kind), as rows written by the old code.</summary>
  private static async Task<IReadOnlyList<LegacyIncome>> SeedHistoryWithTheOldSchemaAsync(AppDbContext db)
  {
    var users = Enumerable.Range(1, 3).Select(i => new User($"Barbero {i}", $"b{i}-{Guid.NewGuid():N}@example.com", "hash", DateTime.UtcNow)).ToArray();
    var staff = users.Select((user, i) => new StaffProfile(user.Id, $"Barbero {i + 1}", 30, DateTime.UtcNow)).ToArray();
    var services = new[]
    {
      new Service("Corte", 25000, DateTime.UtcNow, 33),  // 33% exercises the truncating split
      new Service("Barba", 15000, DateTime.UtcNow, 0),
      new Service("Tinte", 40000, DateTime.UtcNow, 100),
    };
    db.Users.AddRange(users);
    db.StaffProfiles.AddRange(staff);
    db.Services.AddRange(services);
    db.ExpenseEntries.Add(new ExpenseEntry(null, "Arriendo", 900000, new DateOnly(2026, 6, 5), Guid.NewGuid(), DateTime.UtcNow));
    db.ExpenseEntries.Add(new ExpenseEntry(null, "Insumos", 120000, new DateOnly(2026, 3, 15), Guid.NewGuid(), DateTime.UtcNow));
    await db.SaveChangesAsync();

    var rows = new List<LegacyIncome>();
    var random = new Random(20261028); // deterministic
    for (var i = 0; i < 60; i++)
    {
      var service = services[i % services.Length];
      var month = 1 + (i % 12);
      var day = month == 3 && i % 2 == 0 ? 15 : 1 + random.Next(28);
      var row = new LegacyIncome(
          Guid.NewGuid(),
          service.Id,
          service.Name,
          service.BasePrice,
          staff[random.Next(staff.Length)].Id,
          i % 11 == 0 ? 0 : 1000 * random.Next(5, 60) + 7, // odd amounts make the truncation visible
          i % 4 == 0,
          month == 6 && i % 3 == 0 ? new DateOnly(2026, 6, 1 + (i % 28)) : new DateOnly(2026, month, day),
          i % 9 == 0,
          service.BusinessPercentage);
      rows.Add(row);

      await db.Database.ExecuteSqlInterpolatedAsync($"""
          INSERT INTO public.income_entries
            ("Id", "ServiceId", "ServiceNameSnapshot", "BasePriceSnapshot", "StaffProfileId", "Amount", "IsPromo",
             "OccurredOn", "CreatedByUserId", "IsDeleted", "CreatedAt", "BusinessPercentageSnapshot")
          VALUES
            ({row.Id}, {row.ServiceId}, {row.ServiceName}, {row.BasePrice}, {row.StaffProfileId}, {row.Amount}, {row.IsPromo},
             {row.OccurredOn}, {Guid.NewGuid()}, {row.IsDeleted}, {DateTime.UtcNow}, {row.BusinessPercentage})
          """);
    }

    return rows;
  }

  /// <summary>The report as the code before AddIncomeKind computed it (ReportsQueryService at commit d0361fa).</summary>
  private static LegacySummary LegacyReport(IReadOnlyList<LegacyIncome> rows, DateOnly from, DateOnly to)
  {
    var incomes = rows.Where(row => !row.IsDeleted && row.OccurredOn >= from && row.OccurredOn <= to).ToList();
    var expenses = new[] { (Amount: 900000L, On: new DateOnly(2026, 6, 5)), (Amount: 120000L, On: new DateOnly(2026, 3, 15)) }
        .Where(expense => expense.On >= from && expense.On <= to)
        .ToList();

    var totalIncome = incomes.Sum(row => (long)row.Amount);
    var promoIncome = incomes.Where(row => row.IsPromo).Sum(row => (long)row.Amount);
    var totalExpenses = expenses.Sum(expense => expense.Amount);

    var byProfessional = incomes
        .GroupBy(row => row.StaffProfileId)
        .Select(group => (
            StaffProfileId: group.Key,
            IncomeTotal: group.Sum(row => (long)row.Amount),
            IncomeCount: group.Count(),
            BusinessTotal: group.Sum(row => (long)row.BusinessAmount),
            ProfessionalTotal: group.Sum(row => (long)(row.Amount - row.BusinessAmount))))
        .ToList();

    var trend = new List<(string Bucket, long Income, long Expenses)>();
    if (to.DayNumber - from.DayNumber + 1 <= 62)
    {
      for (var day = from; day <= to; day = day.AddDays(1))
      {
        var current = day;
        trend.Add((
            current.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            incomes.Where(row => row.OccurredOn == current).Sum(row => (long)row.Amount),
            expenses.Where(expense => expense.On == current).Sum(expense => expense.Amount)));
      }
    }
    else
    {
      for (var month = new DateOnly(from.Year, from.Month, 1); month <= new DateOnly(to.Year, to.Month, 1); month = month.AddMonths(1))
      {
        var start = month;
        var end = month.AddMonths(1);
        trend.Add((
            start.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
            incomes.Where(row => row.OccurredOn >= start && row.OccurredOn < end).Sum(row => (long)row.Amount),
            expenses.Where(expense => expense.On >= start && expense.On < end).Sum(expense => expense.Amount)));
      }
    }

    return new LegacySummary(
        totalIncome,
        totalExpenses,
        totalIncome - totalExpenses,
        incomes.Count,
        promoIncome,
        totalIncome - promoIncome,
        incomes.Sum(row => (long)row.BusinessAmount),
        incomes.Sum(row => (long)(row.Amount - row.BusinessAmount)),
        byProfessional,
        trend);
  }

  private sealed record LegacyIncome(
      Guid Id,
      Guid ServiceId,
      string ServiceName,
      int BasePrice,
      Guid StaffProfileId,
      int Amount,
      bool IsPromo,
      DateOnly OccurredOn,
      bool IsDeleted,
      int BusinessPercentage)
  {
    /// <summary>The pre-migration split: truncated business share, remainder to the professional.</summary>
    public int BusinessAmount => (int)((long)Amount * BusinessPercentage / 100);
  }

  private sealed record LegacySummary(
      long TotalIncome,
      long TotalExpenses,
      long NetProfit,
      int IncomeCount,
      long PromoIncome,
      long NormalIncome,
      long BusinessIncome,
      long ProfessionalIncome,
      IReadOnlyList<(Guid StaffProfileId, long IncomeTotal, int IncomeCount, long BusinessTotal, long ProfessionalTotal)> ByProfessional,
      IReadOnlyList<(string Bucket, long Income, long Expenses)> Trend);
}
