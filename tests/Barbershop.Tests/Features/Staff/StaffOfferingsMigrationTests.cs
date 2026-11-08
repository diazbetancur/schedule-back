using System.Globalization;
using Barbershop.Application.ServiceRecords;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Common;
using Barbershop.Domain.Customers;
using Barbershop.Domain.ServiceRecords;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.ServiceRecords;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Barbershop.Tests.Features.Staff;

/// <summary>
/// AddStaffServiceOfferings and AddServiceToAppointmentsAndRecords (ADR-0016 B2–B4) over a database with history:
/// professionals (one inactive) with different default durations, appointments, a legacy staff service, a pending and
/// a confirmed service record with its income. The data is created at the latest migration and the schema is rolled
/// back to B1, which is what production has before the deploy. Applying both migrations must only add the authorized
/// columns (all null on existing rows) and one provisional "Corte" offering per professional at their default
/// duration (F2); rolling back must restore every table exactly.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "StaffOfferingsMigration")]
public sealed class StaffOfferingsMigrationTests
{
    private const string MigrationBeforeOfferings = "20261106215959_AddGeneralServicesAndCategories";

    private static readonly Dictionary<string, string[]> NewColumns = new()
    {
        ["appointments"] = ["GeneralServiceId", "GeneralServiceNameSnapshot", "ReservedDurationMinutes"],
        ["service_records"] = ["GeneralServiceId", "ApprovedGeneralServiceId"],
    };

    private readonly PostgresContainerFixture _postgres;

    public StaffOfferingsMigrationTests(PostgresContainerFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task ExistingHistoryIsUntouched_EveryProfessionalGetsCorte_AndTheRollbackRestoresEveryTable()
    {
        if (!_postgres.IsAvailable) return; // Only with PostgresContainerFixture.AllowSkipVariable set on purpose.

        var staff = await SeedHistoryAtLatestAsync();

        await using var db = CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeOfferings);
        Assert.False(await TableExistsAsync(db, "staff_service_offerings"));
        Assert.False(await ColumnExistsAsync(db, "appointments", "ReservedDurationMinutes"));
        var before = await SnapshotAllTablesAsync(db);
        Assert.Equal(3, before["staff_profiles"].Count);
        Assert.Equal(2, before["appointments"].Count);
        Assert.Equal(2, before["service_records"].Count);
        Assert.Single(before["staff_services"]);
        Assert.True(before["income_entries"].Count >= 1, "the confirmed record's income");

        await db.GetService<IMigrator>().MigrateAsync();

        // Every existing table keeps its rows byte for byte, apart from the new columns (the product tables come from B5).
        var after = await SnapshotAllTablesAsync(db, exclude: NewColumns);
        Assert.Equal(before.Keys.Order(), after.Keys.Except(["staff_service_offerings", "product_categories", "products"]).Order());
        foreach (var (table, rows) in before)
        {
            Assert.True(rows.SequenceEqual(after[table]), $"{table} changed");
        }

        // The new columns are empty on every existing row: appointments and records are not backfilled.
        foreach (var (table, columns) in NewColumns)
        {
            foreach (var column in columns)
            {
                Assert.Equal(0, await db.Database.SqlQueryRaw<int>(
                    $"SELECT count(*)::int AS \"Value\" FROM public.\"{table}\" WHERE \"{column}\" IS NOT NULL").SingleAsync());
            }
        }

        // F2: exactly one active "Corte" offering per professional (also the inactive one), at their default duration.
        var offerings = await db.StaffServiceOfferings.AsNoTracking().ToListAsync();
        Assert.Equal(staff.Count, offerings.Count);
        foreach (var (staffProfileId, defaultDuration) in staff)
        {
            var offering = Assert.Single(offerings, item => item.StaffProfileId == staffProfileId);
            Assert.Equal(
                (ServiceCatalogDefaults.CorteGeneralServiceId, defaultDuration, true, (DateTime?)null),
                (offering.GeneralServiceId, offering.DurationMinutes, offering.IsActive, offering.UpdatedAt));
        }

        // The constraints hold: one offering per professional and service, duration 10-240, appointments keep their
        // service, name and duration together, and a record names a rate or a general service.
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.staff_service_offerings ("Id", "StaffProfileId", "GeneralServiceId", "DurationMinutes", "IsActive", "CreatedAt")
            VALUES ({Guid.NewGuid()}, {staff[0].StaffProfileId}, {ServiceCatalogDefaults.CorteGeneralServiceId}, 30, true, now())
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        var tooShort = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE public.staff_service_offerings SET \"DurationMinutes\" = 5"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, tooShort.SqlState);
        var halfService = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE public.appointments SET \"GeneralServiceId\" = {ServiceCatalogDefaults.CorteGeneralServiceId}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, halfService.SqlState);
        var nothingRegistered = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE public.service_records SET \"ServiceId\" = NULL"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, nothingRegistered.SqlState);

        // Rolling back restores the previous schema and every row.
        await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeOfferings);
        Assert.False(await TableExistsAsync(db, "staff_service_offerings"));
        Assert.False(await ColumnExistsAsync(db, "service_records", "GeneralServiceId"));
        Assert.Equal("NO", await db.Database.SqlQuery<string>($"""
            SELECT is_nullable AS "Value" FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'service_records' AND column_name = 'ServiceId'
            """).SingleAsync());
        var rolledBack = await SnapshotAllTablesAsync(db);
        Assert.Equal(before.Keys.Order(), rolledBack.Keys.Order());
        foreach (var (table, rows) in before)
        {
            Assert.True(rows.SequenceEqual(rolledBack[table]), $"{table} changed after the rollback");
        }

        await db.GetService<IMigrator>().MigrateAsync();
    }

    [Fact]
    public async Task TheRollback_StopsWhenARecordOnlyNamesAGeneralService()
    {
        if (!_postgres.IsAvailable) return;
        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();

        var now = DateTime.UtcNow;
        var user = new User("Alex Barbero", "alex@example.com", "hash", now);
        var staff = new StaffProfile(user.Id, "Alex", 30, now);
        db.Users.Add(user);
        db.StaffProfiles.Add(staff);
        db.ServiceRecords.Add(new ServiceRecord(
            Guid.NewGuid(), 0, null, staff.Id, null, "Corte", DateOnly.FromDateTime(now), null, null, null, user.Id, now,
            ServiceCatalogDefaults.CorteGeneralServiceId));
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync(MigrationBeforeOfferings));
        Assert.Contains("Cannot roll back AddServiceToAppointmentsAndRecords", exception.ToString());
        Assert.True(await ColumnExistsAsync(db, "service_records", "GeneralServiceId"));
    }

    /// <summary>History created at the latest migration (see the class summary). Returns each professional's default duration.</summary>
    private async Task<List<(Guid StaffProfileId, int DefaultDuration)>> SeedHistoryAtLatestAsync()
    {
        await using var context = CreateContext();
        await context.Database.EnsureDeletedAsync();
        await context.Database.MigrateAsync();

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(BogotaClock.ToLocal(now));
        var admin = new User("Admin Principal", "admin@example.com", "hash", now);
        var alexUser = new User("Alex Barbero", "alex@example.com", "hash", now);
        var betoUser = new User("Beto Barbero", "beto@example.com", "hash", now);
        var oldUser = new User("Ciro Barbero", "ciro@example.com", "hash", now);
        var customerUser = new User("Carla Cliente", "carla@example.com", "hash", now);
        context.Users.AddRange(admin, alexUser, betoUser, oldUser, customerUser);
        var alex = new StaffProfile(alexUser.Id, "Alex", 30, now);
        var beto = new StaffProfile(betoUser.Id, "Beto", 45, now);
        var ciro = new StaffProfile(oldUser.Id, "Ciro", 20, now);
        ciro.UpdateDetails("Ciro", null, null, null, null, 20, isActive: false, now);
        context.StaffProfiles.AddRange(alex, beto, ciro);
        // The legacy table keeps its row: nothing reads it any more, nothing deletes it.
        context.StaffServices.Add(new StaffService(alex.Id, "Corte clásico", 30, 25000m, now, "Legado"));

        var haircut = new Service("Corte clásico", 25000, now, 30);
        context.Services.Add(haircut);
        var profile = new CustomerProfile("Carla Cliente", "3001234567", "carla@example.com", admin.Id, now);
        profile.LinkAccount(customerUser.Id, admin.Id, now);
        context.Customers.Add(profile);
        context.Appointments.AddRange(
            new Appointment(alex.Id, "Carla Cliente", now.AddDays(1), now.AddDays(1).AddMinutes(30), AppointmentStatus.Confirmed,
                AppointmentSource.CustomerBooking, now, customerUser.Id),
            new Appointment(beto.Id, "Walk in", now.AddDays(-1), now.AddDays(-1).AddMinutes(45), AppointmentStatus.Completed,
                AppointmentSource.Manual, now));

        // Records registered with a rate, as before B4: one confirmed (with its income), one pending.
        var paid = new ServiceRecord(Guid.NewGuid(), 0, null, alex.Id, haircut.Id, haircut.Name, today, profile.Id, profile.FullName, null, alexUser.Id, now);
        var pending = new ServiceRecord(Guid.NewGuid(), 0, null, beto.Id, haircut.Id, haircut.Name, today, null, "Paso", null, betoUser.Id, now);
        context.ServiceRecords.AddRange(paid, pending);
        await context.SaveChangesAsync();

        var records = new ServiceRecordsService(
            context, new MembershipLedgerService(context, new MembershipSalesService(context, TimeProvider.System), TimeProvider.System), TimeProvider.System);
        await records.ApproveAsync(admin.Id, paid.Id, new ServiceRecordApproveRequest(
            paid.Version, "Paid", haircut.Id, alex.Id, profile.Id, today, 25000, null, false, null));

        // Offerings created at the latest schema disappear with the rollback; only the migration's seed must remain.
        context.StaffServiceOfferings.Add(new StaffServiceOffering(alex.Id, ServiceCatalogDefaults.CorteGeneralServiceId, 60, true, now));
        await context.SaveChangesAsync();

        return [(alex.Id, 30), (beto.Id, 45), (ciro.Id, 20)];
    }

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
}
