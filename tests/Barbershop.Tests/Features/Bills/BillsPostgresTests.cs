using Barbershop.Application.Bills;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Domain.Bills;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Bills;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Barbershop.Tests.Features.Bills;

/// <summary>
/// ADR-0020 stages 2 and 3 on real PostgreSQL: the migration keeps every existing row (its non-additive CHECK changes
/// admit them all) and rolls back; the audit trail is append-only and the line shapes are enforced; simultaneous closes
/// take consecutive numbers without gaps; two simultaneous approvals post once; a stale version is a conflict.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "BillsPostgres")]
public sealed class BillsPostgresTests
{
  private const string MigrationBeforeBills = "20261115124512_AddInventory";
  private const string MigrationBeforeReversals = "20261115155934_AddBillsAndApprovals";

  private static readonly string[] NewTables =
      ["bills", "bill_lines", "bill_events", "bill_approvals", "bill_postings", "payment_methods", "bill_number_series"];

  private readonly PostgresContainerFixture _postgres;

  public BillsPostgresTests(PostgresContainerFixture postgres) => _postgres = postgres;

  [Fact]
  public async Task TheMigration_KeepsEveryExistingRow_SeedsOnlyItsNewTables_AndItsRollbackRestoresEveryTable()
  {
    if (!_postgres.IsAvailable) return; // Only with PostgresContainerFixture.AllowSkipVariable set on purpose.

    await using (var seed = CreateContext())
    {
      await seed.Database.EnsureDeletedAsync();
      await seed.Database.MigrateAsync();
      var now = DateTime.UtcNow;
      var admin = new User("Admin Principal", "admin@example.com", "hash", now);
      var barberUser = new User("Alex Barbero", "alex@example.com", "hash", now);
      seed.Users.AddRange(admin, barberUser);
      var staff = new StaffProfile(barberUser.Id, "Alex", 30, now);
      seed.StaffProfiles.Add(staff);
      var rate = new Service("Corte", 25000, now, 40);
      seed.Services.Add(rate);
      // A manual service income and a membership sale, as stored before the bills.
      seed.IncomeEntries.Add(new IncomeEntry(rate.Id, rate.Name, rate.BasePrice, staff.Id, 20000, true, DateOnly.FromDateTime(now), admin.Id, now, 40));
      seed.IncomeEntries.Add(IncomeEntry.CreateMembershipSale(250000, DateOnly.FromDateTime(now), admin.Id, now));
      await seed.SaveChangesAsync();
    }

    await using var db = CreateContext();
    await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeBills);
    foreach (var table in NewTables)
    {
      Assert.False(await TableExistsAsync(db, table));
    }

    var before = await MigrationSnapshotReader.ReadAsync(db);
    Assert.Equal(2, before["income_entries"].Count);

    await db.GetService<IMigrator>().MigrateAsync();

    var after = await MigrationSnapshotReader.ReadAsync(db);
    Assert.Equal(before.Keys.Order(), after.Keys.Except(NewTables).Order());
    foreach (var (table, rows) in before)
    {
      // income_entries and loyalty_redemptions gain nullable columns: NULL on every existing row.
      Assert.True(MigrationSnapshot.SameRows(rows, after[table]), $"{table} changed");
    }

    foreach (var table in new[] { "bills", "bill_lines", "bill_events", "bill_approvals", "bill_postings" })
    {
      Assert.Empty(after[table]);
    }

    Assert.Equal(5, after["payment_methods"].Count);
    Assert.Contains("NextNumber=1", Assert.Single(after["bill_number_series"]));

    // The replaced CHECK still refuses a product sale without its product, or one not posted by a bill.
    var invalid = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""
        INSERT INTO public.income_entries ("Id", "Kind", "BasePriceSnapshot", "BusinessPercentageSnapshot", "Amount", "IsPromo", "OccurredOn", "CreatedByUserId", "IsDeleted", "CreatedAt")
        SELECT gen_random_uuid(), 'ProductSale', 0, 100, 1000, false, current_date, "Id", false, now() FROM public.users LIMIT 1
        """));
    Assert.Equal(PostgresErrorCodes.CheckViolation, invalid.SqlState);

    await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeBills);
    var rolledBack = await MigrationSnapshotReader.ReadAsync(db);
    Assert.Equal(before.Keys.Order(), rolledBack.Keys.Order());
    foreach (var (table, rows) in before)
    {
      Assert.True(rows.SequenceEqual(rolledBack[table]), $"{table} changed after the rollback");
    }

    await db.GetService<IMigrator>().MigrateAsync();
  }

  [Fact]
  public async Task TheAuditTrail_IsAppendOnly_AndTheLineShapesAreChecked()
  {
    if (!_postgres.IsAvailable) return;
    var seeded = await ResetAndSeedAsync();
    var bill = await AddServiceAsync(seeded, await OpenAsync(seeded));

    await using var db = CreateContext();
    var update = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE public.bill_events SET \"Reason\" = 'x'"));
    Assert.Equal(PostgresErrorCodes.RestrictViolation, update.SqlState);
    var delete = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM public.bill_events"));
    Assert.Equal(PostgresErrorCodes.RestrictViolation, delete.SqlState);

    // A discount without its reason, or a tip without how it was paid, does not fit the table.
    var discount = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.bill_lines ("Id", "BillId", "Position", "Type", "NameSnapshot", "Quantity", "UnitPrice", "IsCourtesy", "Amount", "CreatedAt")
        VALUES ({Guid.NewGuid()}, {bill.Id}, 9, 'Discount', 'Descuento', 1, 1000, false, -1000, now())
        """));
    Assert.Equal(PostgresErrorCodes.CheckViolation, discount.SqlState);
    var tip = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.bill_lines ("Id", "BillId", "Position", "Type", "StaffProfileId", "NameSnapshot", "Quantity", "UnitPrice", "IsCourtesy", "Amount", "CreatedAt")
        VALUES ({Guid.NewGuid()}, {bill.Id}, 9, 'Tip', {seeded.StaffId}, 'Propina', 1, 1000, false, 1000, now())
        """));
    Assert.Equal(PostgresErrorCodes.CheckViolation, tip.SqlState);

    // The totals stay consistent with each other.
    var total = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
        $"UPDATE public.bills SET \"Total\" = 1 WHERE \"Id\" = {bill.Id}"));
    Assert.Equal(PostgresErrorCodes.CheckViolation, total.SqlState);
  }

  [Fact]
  public async Task AStaleVersion_IsAConflict()
  {
    if (!_postgres.IsAvailable) return;
    var seeded = await ResetAndSeedAsync();
    var opened = await OpenAsync(seeded);
    var current = await AddServiceAsync(seeded, opened);

    Assert.NotEqual(opened.Version, current.Version);
    await using var context = CreateContext();
    var stale = await Assert.ThrowsAsync<ConflictException>(() => AddServiceAsync(seeded, opened));
    Assert.Equal(BillsService.StaleMessage, stale.Message);
  }

  [Fact]
  public async Task SimultaneousCloses_TakeConsecutiveNumbersWithoutGaps()
  {
    if (!_postgres.IsAvailable) return;
    var seeded = await ResetAndSeedAsync();
    var bills = new List<BillView>();
    for (var index = 0; index < 6; index++)
    {
      bills.Add(await AddServiceAsync(seeded, await OpenAsync(seeded)));
    }

    // One of them fails validation (no payment method): it must not spend a number.
    var outcomes = await Task.WhenAll(bills.Select(async (bill, index) =>
    {
      await using var context = CreateContext();
      try
      {
        return (await Service(context).CloseAsync(seeded.AdminId, bill.Id, new BillCloseRequest(bill.Version, index == 0 ? null : seeded.Cash))).Number;
      }
      catch (ValidationProblemException)
      {
        return null;
      }
    }));

    Assert.Single(outcomes, number => number is null);
    Assert.Equal(["C-000001", "C-000002", "C-000003", "C-000004", "C-000005"], outcomes.OfType<string>().Order());
    await using var db = CreateContext();
    Assert.Equal(6, (await db.BillNumberSeries.AsNoTracking().SingleAsync()).NextNumber);
  }

  [Fact]
  public async Task TwoSimultaneousApprovals_PostOnce_AndTheOtherIsAConflict()
  {
    if (!_postgres.IsAvailable) return;
    var seeded = await ResetAndSeedAsync();
    var bill = await AddServiceAsync(seeded, await OpenAsync(seeded));
    await using (var context = CreateContext())
    {
      bill = await Service(context).CloseAsync(seeded.AdminId, bill.Id, new BillCloseRequest(bill.Version, seeded.Cash));
    }

    var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
    {
      await using var context = CreateContext();
      try
      {
        await Service(context).ApproveAsync(seeded.AdminId, bill.Id, new BillApproveRequest(bill.Version, Guid.NewGuid()));
        return (Exception?)null;
      }
      catch (Exception exception)
      {
        return exception;
      }
    }));

    Assert.Single(outcomes, outcome => outcome is null);
    Assert.All(outcomes.OfType<Exception>(), outcome => Assert.IsType<ConflictException>(outcome));
    await using var db = CreateContext();
    var income = await db.IncomeEntries.AsNoTracking().SingleAsync();
    Assert.Equal((25000, bill.Id), (income.Amount, income.BillId!.Value));
    Assert.Single(await db.BillApprovals.AsNoTracking().ToListAsync());
    Assert.Equal(BillStatus.Approved, (await db.Bills.AsNoTracking().SingleAsync()).Status);
  }

  [Fact]
  public async Task TheReversalMigration_KeepsEveryRow_AndOnlyACounterEntryMayBeNegative()
  {
    if (!_postgres.IsAvailable) return;
    var seeded = await ResetAndSeedAsync();
    var bill = await AddServiceAsync(seeded, await OpenAsync(seeded));
    await using (var context = CreateContext())
    {
      bill = await Service(context).CloseAsync(seeded.AdminId, bill.Id, new BillCloseRequest(bill.Version, seeded.Cash));
    }

    await using (var context = CreateContext())
    {
      await Service(context).ApproveAsync(seeded.AdminId, bill.Id, new BillApproveRequest(bill.Version, Guid.NewGuid()));
    }

    await using var db = CreateContext();
    await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeReversals);
    var before = await MigrationSnapshotReader.ReadAsync(db);
    Assert.Single(before["income_entries"]);
    await db.GetService<IMigrator>().MigrateAsync();
    var after = await MigrationSnapshotReader.ReadAsync(db);
    Assert.Equal(before.Keys.Order(), after.Keys.Order());
    foreach (var (table, rows) in before)
    {
      Assert.True(MigrationSnapshot.SameRows(rows, after[table]), $"{table} changed");
    }

    // A negative amount that does not reverse anything is still refused.
    var negative = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""
        UPDATE public.income_entries SET "Amount" = -1
        """));
    Assert.Equal(PostgresErrorCodes.CheckViolation, negative.SqlState);

    await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeReversals);
    var rolledBack = await MigrationSnapshotReader.ReadAsync(db);
    foreach (var (table, rows) in before)
    {
      Assert.True(rows.SequenceEqual(rolledBack[table]), $"{table} changed after the rollback");
    }

    await db.GetService<IMigrator>().MigrateAsync();
  }

  [Fact]
  public async Task SimultaneousUnapprovalsAndApprovals_ReverseOnce_AndTheCycleNetsToOneApproval()
  {
    if (!_postgres.IsAvailable) return;
    var seeded = await ResetAndSeedAsync();
    var bill = await AddServiceAsync(seeded, await OpenAsync(seeded));
    await using (var context = CreateContext())
    {
      bill = await Service(context).CloseAsync(seeded.AdminId, bill.Id, new BillCloseRequest(bill.Version, seeded.Cash));
    }

    await using (var context = CreateContext())
    {
      bill = await Service(context).ApproveAsync(seeded.AdminId, bill.Id, new BillApproveRequest(bill.Version, Guid.NewGuid()));
    }

    var version = bill.Version;
    var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(async index =>
    {
      await using var context = CreateContext();
      try
      {
        if (index == 4)
        {
          await Service(context).ApproveAsync(seeded.AdminId, bill.Id, new BillApproveRequest(version, Guid.NewGuid()));
        }
        else
        {
          await Service(context).UnapproveAsync(seeded.AdminId, bill.Id, new BillUnapproveRequest(version, Guid.NewGuid(), "Corrección"));
        }

        return (Exception?)null;
      }
      catch (Exception exception)
      {
        return exception;
      }
    }));

    Assert.Single(outcomes, outcome => outcome is null);
    Assert.All(outcomes.OfType<Exception>(), outcome => Assert.IsType<ConflictException>(outcome));
    await using (var db = CreateContext())
    {
      Assert.Equal(0, await db.IncomeEntries.SumAsync(income => income.Amount));
      Assert.Single(await db.IncomeEntries.Where(income => income.ReversesIncomeEntryId != null).ToListAsync());
      var stored = await db.Bills.AsNoTracking().SingleAsync();
      Assert.Equal(BillStatus.Unapproved, stored.Status);
      version = stored.Version;
    }

    await using (var context = CreateContext())
    {
      await Service(context).ApproveAsync(seeded.AdminId, bill.Id, new BillApproveRequest(version, Guid.NewGuid()));
    }

    await using var check = CreateContext();
    Assert.Equal(25000, await check.IncomeEntries.SumAsync(income => income.Amount));
    Assert.Equal(2, await check.BillApprovals.CountAsync());
  }

  private sealed record Seeded(Guid AdminId, Guid StaffId, Guid RateId, Guid Cash);

  private async Task<Seeded> ResetAndSeedAsync()
  {
    await using var context = CreateContext();
    await context.Database.EnsureDeletedAsync();
    await context.Database.MigrateAsync();
    var now = DateTime.UtcNow;
    var admin = new User("Admin Principal", "admin@example.com", "hash", now);
    var barberUser = new User("Alex Barbero", "alex@example.com", "hash", now);
    context.Users.AddRange(admin, barberUser);
    var adminRole = new Role(RoleNames.Admin, isSystemRole: true);
    context.Roles.Add(adminRole);
    context.UserRoles.Add(new UserRole(admin.Id, adminRole.Id, now));
    var staff = new StaffProfile(barberUser.Id, "Alex", 30, now);
    context.StaffProfiles.Add(staff);
    var rate = new Service("Corte", 25000, now, 40);
    context.Services.Add(rate);
    await context.SaveChangesAsync();
    var cash = await context.PaymentMethods.Where(method => method.Name == "Efectivo").Select(method => method.Id).SingleAsync();
    return new Seeded(admin.Id, staff.Id, rate.Id, cash);
  }

  private async Task<BillView> OpenAsync(Seeded seeded)
  {
    await using var context = CreateContext();
    return await Service(context).OpenAsync(seeded.AdminId, new BillOpenRequest(Guid.NewGuid(), CustomerName: "Cliente de paso", StaffProfileId: seeded.StaffId));
  }

  private async Task<BillView> AddServiceAsync(Seeded seeded, BillView bill)
  {
    await using var context = CreateContext();
    return await Service(context).AddLineAsync(seeded.AdminId, bill.Id, new BillLineRequest(
        bill.Version, "Service", seeded.StaffId, ServiceCatalogDefaults.CorteGeneralServiceId, seeded.RateId));
  }

  private static BillsService Service(AppDbContext context)
      => new(context, new MembershipLedgerService(context, new MembershipSalesService(context, TimeProvider.System), TimeProvider.System), TimeProvider.System);

  private AppDbContext CreateContext()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(_postgres.ConnectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
        .Options;
    return new AppDbContext(options);
  }

  private static async Task<bool> TableExistsAsync(AppDbContext db, string table)
      => await db.Database.SqlQuery<int>($"""
          SELECT count(*)::int AS "Value" FROM information_schema.tables WHERE table_schema = 'public' AND table_name = {table}
          """).SingleAsync() == 1;
}
