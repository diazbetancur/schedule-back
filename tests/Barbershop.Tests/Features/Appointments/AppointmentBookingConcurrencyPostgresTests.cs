using System.Data.Common;
using Barbershop.Application.Appointments;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Notifications;
using Barbershop.Domain.Common;
using Barbershop.Domain.Scheduling;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Appointments;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Barbershop.Tests.Features.Appointments;

/// <summary>
/// ADR-0016 T7 on real PostgreSQL: two customers book overlapping times of different services (and so different
/// lengths) with the same professional at the same moment. Forced deterministically: the first booking is held right
/// after it locks the professional's row, the second is observed waiting on that lock in pg_stat_activity, then the
/// first is released. Exactly one appointment is stored and the other booking is a conflict.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "AppointmentsPostgres")]
public sealed class AppointmentBookingConcurrencyPostgresTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly Guid Corte = ServiceCatalogDefaults.CorteGeneralServiceId;

    private readonly PostgresContainerFixture _postgres;

    public AppointmentBookingConcurrencyPostgresTests(PostgresContainerFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task TwoOverlappingBookingsOfDifferentServices_EndWithOneAppointmentAndOneConflict()
    {
        if (!_postgres.IsAvailable) return; // Only with PostgresContainerFixture.AllowSkipVariable set on purpose.
        var seed = await SeedAsync();

        // Color (45 min) at 9:00 and Corte (30 min) at 9:30 overlap from 9:30 to 9:45.
        var colorAt9 = new CustomerAppointmentCreateRequest(seed.StaffProfileId, seed.ColorId, seed.At(9, 0), "Color");
        var corteAt930 = new CustomerAppointmentCreateRequest(seed.StaffProfileId, Corte, seed.At(9, 30), "Corte");

        var gate = new StatementGate(sql => sql.Contains("staff_profiles", StringComparison.Ordinal) && sql.Contains("FOR UPDATE", StringComparison.Ordinal));
        await using var holderContext = CreateContext(gate);
        await using var contenderContext = CreateContext(null);

        var holding = Task.Run(() => Capture(() => Appointments(holderContext).CreateAsync(seed.FirstCustomerId, colorAt9)));
        await gate.Held.WaitAsync(Timeout);
        var contending = Task.Run(() => Capture(() => Appointments(contenderContext).CreateAsync(seed.SecondCustomerId, corteAt930)));
        await WaitUntilASessionWaitsOnALockAsync();
        Assert.False(contending.IsCompleted);

        gate.Release();
        var holderError = await holding.WaitAsync(Timeout);
        var contenderError = await contending.WaitAsync(Timeout);

        Assert.Null(holderError);
        Assert.IsType<ConflictException>(contenderError);

        await using var verify = CreateContext(null);
        var stored = Assert.Single(await verify.Appointments.AsNoTracking().ToListAsync());
        Assert.Equal(
            (seed.FirstCustomerId, seed.ColorId, "Color", 45, seed.At(9, 0), seed.At(9, 45)),
            (stored.CustomerUserId!.Value, stored.GeneralServiceId!.Value, stored.GeneralServiceNameSnapshot, stored.ReservedDurationMinutes!.Value, stored.StartsAt, stored.EndsAt));

        // Without overlap the second customer books right after it; the database check keeps EndsAt = StartsAt + duration.
        await using var after = CreateContext(null);
        var corteAt945 = await Appointments(after).CreateAsync(seed.SecondCustomerId, corteAt930 with { StartsAtUtc = seed.At(9, 45) });
        Assert.Equal(seed.At(10, 15), corteAt945.EndsAtUtc);
        var broken = await Assert.ThrowsAsync<PostgresException>(() => after.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE public.appointments SET \"EndsAt\" = \"EndsAt\" + interval '5 minutes' WHERE \"Id\" = {corteAt945.Id}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, broken.SqlState);
    }

    private sealed record Seed(Guid StaffProfileId, Guid ColorId, Guid FirstCustomerId, Guid SecondCustomerId, DateOnly Day)
    {
        public DateTime At(int hour, int minute) => BogotaClock.ToUtc(Day, new TimeOnly(hour, minute));
    }

    private async Task<Seed> SeedAsync()
    {
        await using var context = CreateContext(null);
        await context.Database.EnsureDeletedAsync();
        await context.Database.MigrateAsync();

        var now = DateTime.UtcNow;
        var day = DateOnly.FromDateTime(BogotaClock.ToLocal(now)).AddDays(2);
        var barberUser = new User("Alex Barbero", "alex@example.com", "hash", now);
        var first = new User("Ana Cliente", "ana@example.com", "hash", now);
        var second = new User("Beto Cliente", "beto@example.com", "hash", now);
        context.Users.AddRange(barberUser, first, second);
        var staff = new StaffProfile(barberUser.Id, "Alex", 30, now);
        context.StaffProfiles.Add(staff);
        var color = new GeneralService(ServiceCatalogDefaults.BarberiaCategoryId, "Color", null, 1, now);
        context.GeneralServices.Add(color);
        context.StaffAvailabilityRules.Add(new StaffAvailabilityRule(staff.Id, day.DayOfWeek, new TimeOnly(9, 0), new TimeOnly(12, 0)));
        await context.SaveChangesAsync();

        context.StaffServiceOfferings.AddRange(
            new StaffServiceOffering(staff.Id, Corte, 30, true, now),
            new StaffServiceOffering(staff.Id, color.Id, 45, true, now));
        await context.SaveChangesAsync();

        return new Seed(staff.Id, color.Id, first.Id, second.Id, day);
    }

    private static ICustomerAppointmentsService Appointments(AppDbContext context)
        => new AppointmentManagementService(context, TimeProvider.System, new SilentNotifications());

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
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND datname = current_database()", connection);
            if ((long)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, "The second booking never waited on a lock.");
            await Task.Delay(50);
        }
    }

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

    /// <summary>Holds the first command matching the predicate, after it ran (its locks taken), until released.</summary>
    private sealed class StatementGate(Func<string, bool> matches) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Held => _held.Task;

        public void Release() => _released.TrySetResult();

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            await HoldIfMatchingAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            await HoldIfMatchingAsync(command, cancellationToken);
            return result;
        }

        private async Task HoldIfMatchingAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (matches(command.CommandText) && _held.TrySetResult())
            {
                await _released.Task.WaitAsync(Timeout, cancellationToken);
            }
        }
    }

    private sealed class SilentNotifications : IAppointmentNotificationService
    {
        public Task NotifyStaffOfNewAppointmentAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task NotifyStaffOfCustomerCancellationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task NotifyStaffOfAppointmentRescheduledAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task NotifyStaffOfAppointmentCancelledAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task NotifyCustomerOfAppointmentUpdateAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task NotifyCustomerOfAppointmentCancellationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task NotifyCustomerOfAppointmentConfirmationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
