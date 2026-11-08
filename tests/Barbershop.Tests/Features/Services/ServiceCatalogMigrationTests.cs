using System.Globalization;
using Barbershop.Application.Auth;
using Barbershop.Application.Loyalty.Redemptions;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Application.ServiceRecords;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Common;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.ServiceRecords;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Loyalty;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.ServiceRecords;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Barbershop.Tests.Features.Services;

/// <summary>
/// AddGeneralServicesAndCategories over a database with real history, built with the application's own flows:
/// rates in every state, a membership sold and consumed, a paid record with its income, an approved redemption of a
/// service reward and an appointment. The data is created at the latest migration and the schema is then rolled back
/// to the previous one, which is exactly what production has before the deploy.
/// Applying the migration must change nothing but the rates' new association, which goes to "Corte" for every rate
/// (even one named like another general service: nothing is inferred from names), and rolling it back must restore
/// every table exactly.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "ServiceCatalogMigration")]
public sealed class ServiceCatalogMigrationTests
{
  private const string MigrationBeforeCatalog = "20261104171811_AddLoyaltyRedemptions";
  // The latest schema also has the later ADR-0016 migrations (B2-B5): their tables and nullable columns are left out.
  private static readonly string[] NewTables =
      ["service_categories", "general_services", "staff_service_offerings", "product_categories", "products"];

  private readonly PostgresContainerFixture _postgres;

  public ServiceCatalogMigrationTests(PostgresContainerFixture postgres) => _postgres = postgres;

  [Fact]
  public async Task ExistingHistory_IsUntouched_EveryRateGoesToCorte_AndTheRollbackRestoresEveryTable()
  {
    if (!_postgres.IsAvailable) return; // Only with PostgresContainerFixture.AllowSkipVariable set on purpose.

    var seed = await SeedHistoryAtLatestAsync();

    await using var db = CreateContext();
    await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeCatalog);
    Assert.False(await ColumnExistsAsync(db, "services", "GeneralServiceId"));
    var before = await SnapshotAllTablesAsync(db);
    Assert.True(before["services"].Count == 4, "four rates seeded");
    Assert.True(before["income_entries"].Count >= 1, "the paid service's income");
    Assert.Single(before["membership_consumptions"]);
    Assert.Single(before["loyalty_redemptions"]);
    Assert.Single(before["appointments"]);

    await db.GetService<IMigrator>().MigrateAsync();

    // Only the rates gained a column; every other table, including all history, is byte-for-byte the same.
    var after = await SnapshotAllTablesAsync(db, exclude: new Dictionary<string, string[]>
    {
      ["services"] = ["GeneralServiceId"],
      ["appointments"] = ["GeneralServiceId", "GeneralServiceNameSnapshot", "ReservedDurationMinutes"],
      ["service_records"] = ["GeneralServiceId", "ApprovedGeneralServiceId"],
    });
    Assert.Equal(before.Keys.Order(), after.Keys.Except(NewTables).Order());
    foreach (var (table, rows) in before)
    {
      Assert.True(rows.SequenceEqual(after[table]), $"{table} changed");
    }

    // Every rate, whatever its state or name, is attached to the provisional "Corte".
    var assignments = await db.Database.SqlQueryRaw<Guid>(
        "SELECT \"GeneralServiceId\" AS \"Value\" FROM public.services").ToListAsync();
    Assert.Equal(4, assignments.Count);
    Assert.All(assignments, id => Assert.Equal(ServiceCatalogDefaults.CorteGeneralServiceId, id));
    var beard = await db.Services.AsNoTracking().SingleAsync(rate => rate.Id == seed.BeardRateId);
    Assert.Equal(ServiceCatalogDefaults.CorteGeneralServiceId, beard.GeneralServiceId);

    // The new masters hold exactly the two seeded rows.
    var category = Assert.Single(await db.ServiceCategories.AsNoTracking().ToListAsync());
    Assert.Equal(
        (ServiceCatalogDefaults.BarberiaCategoryId, "Barbería", "barberia", true, false),
        (category.Id, category.Name, category.PublicCode, category.IsActive, category.IsDeleted));
    var general = Assert.Single(await db.GeneralServices.AsNoTracking().ToListAsync());
    Assert.Equal(
        (ServiceCatalogDefaults.CorteGeneralServiceId, ServiceCatalogDefaults.BarberiaCategoryId, "Corte", "corte", true, false),
        (general.Id, general.CategoryId, general.Name, general.PublicCode, general.IsActive, general.IsDeleted));

    // The association is enforced: not null, and a general service with rates cannot be physically deleted.
    var nullRate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
        "UPDATE public.services SET \"GeneralServiceId\" = NULL"));
    Assert.Equal(PostgresErrorCodes.NotNullViolation, nullRate.SqlState);
    var deleteCorte = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
        $"DELETE FROM public.general_services WHERE \"Id\" = {ServiceCatalogDefaults.CorteGeneralServiceId}"));
    Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, deleteCorte.SqlState);
    var badCode = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
        $"UPDATE public.general_services SET \"PublicCode\" = {"Con Mayúsculas"}"));
    Assert.Equal(PostgresErrorCodes.CheckViolation, badCode.SqlState);

    // Rolling back restores the previous schema and every row.
    await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeCatalog);
    Assert.False(await ColumnExistsAsync(db, "services", "GeneralServiceId"));
    var rolledBack = await SnapshotAllTablesAsync(db);
    Assert.Equal(before.Keys.Order(), rolledBack.Keys.Order());
    foreach (var (table, rows) in before)
    {
      Assert.True(rows.SequenceEqual(rolledBack[table]), $"{table} changed after the rollback");
    }

    await db.GetService<IMigrator>().MigrateAsync();
  }

  private sealed record Seed(Guid BeardRateId);

  /// <summary>History created with the application at the latest migration (see the class summary).</summary>
  private async Task<Seed> SeedHistoryAtLatestAsync()
  {
    await using var context = CreateContext();
    await context.Database.EnsureDeletedAsync();
    await context.Database.MigrateAsync();

    var now = DateTime.UtcNow;
    var today = DateOnly.FromDateTime(BogotaClock.ToLocal(now));
    var admin = new User("Admin Principal", "admin@example.com", "hash", now);
    var customerUser = new User("Carla Cliente", "carla@example.com", "hash", now);
    var barberUser = new User("Alex Barbero", "alex@example.com", "hash", now);
    context.Users.AddRange(admin, customerUser, barberUser);
    var staff = new StaffProfile(barberUser.Id, "Alex", 30, now);
    context.StaffProfiles.Add(staff);

    // A second general service named like one of the rates, to show the migration never maps by name.
    var beardGeneral = new GeneralService(ServiceCatalogDefaults.BarberiaCategoryId, "Barba", null, 1, now);
    context.GeneralServices.Add(beardGeneral);
    var haircut = new Service("Corte clásico", 25000, now, 30);
    haircut.SetLoyaltyPoints(10, now);
    var beard = new Service("Barba", 15000, now, 40, beardGeneral.Id);
    var oldRate = new Service("Corte anterior", 20000, now, 30);
    oldRate.Deactivate(now);
    var deletedRate = new Service("Corte eliminado", 18000, now, 30);
    deletedRate.MarkDeleted(now);
    context.Services.AddRange(haircut, beard, oldRate, deletedRate);

    var settings = await context.LoyaltySettings.SingleAsync();
    settings.Update(true, 365, admin.Id, now);
    var profile = new CustomerProfile("Carla Cliente", "3001234567", "carla@example.com", admin.Id, now);
    profile.LinkAccount(customerUser.Id, admin.Id, now);
    context.Customers.Add(profile);
    var lot = LoyaltyLot.Accrue(profile.Id, LoyaltySourceType.Adjustment, Guid.NewGuid(), 100, now, today, 365);
    context.LoyaltyLots.Add(lot);
    context.LoyaltyMovements.Add(new LoyaltyMovement(profile.Id, LoyaltyMovementType.AdjustmentCredit, 100, now, lotId: lot.Id, performedByUserId: admin.Id));
    context.Appointments.Add(new Appointment(
        staff.Id, "Carla Cliente", now.AddDays(1), now.AddDays(1).AddMinutes(30), AppointmentStatus.Confirmed,
        AppointmentSource.CustomerBooking, now, customerUser.Id));
    await context.SaveChangesAsync();

    // Membership sold (its income) and consumed once through an approved record.
    var plan = await new MembershipPlanManagementService(context, TimeProvider.System, new FixedUser(admin.Id)).CreateAsync(
        new MembershipPlanUpsertRequest("Oro", null, 250000, "Pool", 10, 90, "La membresía es personal e intransferible.", null,
            [new(haircut.Id, null), new(beard.Id, null)], [], 0));
    var membership = await new MembershipSalesService(context, TimeProvider.System)
        .SellAsync(admin.Id, profile.Id, new MembershipSaleRequest(plan.Id, today, ActivateNow: true));
    var consumed = new ServiceRecord(Guid.NewGuid(), 0, null, staff.Id, haircut.Id, haircut.Name, today, profile.Id, profile.FullName, null, barberUser.Id, now);
    var paid = new ServiceRecord(Guid.NewGuid(), 0, null, staff.Id, beard.Id, beard.Name, today, profile.Id, profile.FullName, null, barberUser.Id, now);
    context.ServiceRecords.AddRange(consumed, paid);
    await context.SaveChangesAsync();

    var records = Records(context);
    await records.ApproveAsync(admin.Id, consumed.Id, new ServiceRecordApproveRequest(
        consumed.Version, "MembershipConsumption", haircut.Id, staff.Id, profile.Id, today, null, membership.Id, false, null));
    await records.ApproveAsync(admin.Id, paid.Id, new ServiceRecordApproveRequest(
        paid.Version, "Paid", beard.Id, staff.Id, profile.Id, today, 15000, null, false, null));

    // A service reward redemption, requested and approved.
    var redemptions = new LoyaltyRedemptionService(context, TimeProvider.System);
    var reward = await redemptions.CreateAsync(admin.Id, new LoyaltyRewardUpsertRequest("Corte gratis", null, "Service", haircut.Id, 30));
    var requested = await redemptions.RequestAsync(customerUser.Id, new LoyaltyRedemptionRequest(reward.Id, Guid.NewGuid()));
    var current = await redemptions.GetAsync(requested.Id);
    await redemptions.ApproveAsync(admin.Id, requested.Id, new LoyaltyRedemptionDecisionRequest(current.Version));

    return new Seed(beard.Id);
  }

  private static ServiceRecordsService Records(AppDbContext context)
      => new(context, new MembershipLedgerService(context, new MembershipSalesService(context, TimeProvider.System), TimeProvider.System), TimeProvider.System);

  private AppDbContext CreateContext()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(_postgres.ConnectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
        .Options;
    return new AppDbContext(options);
  }

  private static async Task<bool> ColumnExistsAsync(AppDbContext db, string table, string column)
      => await db.Database.SqlQuery<int>($"""
          SELECT count(*)::int AS "Value" FROM information_schema.columns
          WHERE table_schema = 'public' AND table_name = {table} AND column_name = {column}
          """).SingleAsync() == 1;

  /// <summary>Every row of every public table (except the migrations history), ordered by its whole content.</summary>
  private static async Task<Dictionary<string, List<string>>> SnapshotAllTablesAsync(
      AppDbContext db, Dictionary<string, string[]>? exclude = null)
  {
    var connection = (NpgsqlConnection)db.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open)
    {
      await connection.OpenAsync();
    }

    var tables = new List<string>();
    await using (var list = new NpgsqlCommand(
        "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' AND table_name <> '__EFMigrationsHistory' ORDER BY table_name",
        connection))
    await using (var reader = await list.ExecuteReaderAsync())
    {
      while (await reader.ReadAsync())
      {
        tables.Add(reader.GetString(0));
      }
    }

    var snapshot = new Dictionary<string, List<string>>();
    foreach (var table in tables)
    {
      var skip = exclude?.GetValueOrDefault(table) ?? [];
      await using var command = new NpgsqlCommand($"SELECT * FROM public.\"{table}\"", connection);
      await using var reader = await command.ExecuteReaderAsync();
      var rows = new List<string>();
      while (await reader.ReadAsync())
      {
        rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
            .Where(index => !skip.Contains(reader.GetName(index)))
            .OrderBy(index => reader.GetName(index), StringComparer.Ordinal)
            .Select(index => $"{reader.GetName(index)}={(reader.IsDBNull(index) ? "null" : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))}")));
      }

      rows.Sort(StringComparer.Ordinal);
      snapshot[table] = rows;
    }

    return snapshot;
  }

  private sealed class FixedUser(Guid? userId) : ICurrentUserAccessor
  {
    public Guid? UserId { get; } = userId;
  }
}
