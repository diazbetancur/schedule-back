using Barbershop.Application.Appointments;
using Barbershop.Application.Availability;
using Barbershop.Application.Notifications;
using Barbershop.Application.Staff;
using Barbershop.Application.Staff.Admin;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Common;
using Barbershop.Infrastructure.Appointments;
using Barbershop.Infrastructure.Availability;
using Barbershop.Infrastructure.Configuration;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Staff;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Barbershop.Tests.Features.Appointments;

public sealed class StaffAppointmentsTests : IDisposable
{
    // 10:15 AM Bogota local time -> 15:15 UTC (Bogota is UTC-5).
    private static readonly DateTimeOffset CurrentUtc = new(2026, 1, 5, 15, 15, 0, TimeSpan.Zero);

    private readonly AppDbContext _dbContext;
    private readonly IAdminStaffService _adminStaffService;
    private readonly IAdminStaffAvailabilityService _adminStaffAvailabilityService;
    private readonly IStaffAppointmentsService _staffAppointmentsService;
    private readonly IAdminAppointmentsService _adminAppointmentsService;
    private readonly IPublicAvailabilityService _publicAvailabilityService;

    public StaffAppointmentsTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        _dbContext = new AppDbContext(options);

        var passwordHasher = new PasswordHasher<object>();
        var hostEnvironment = new TestHostEnvironment();
        var timeProvider = new FixedTimeProvider(CurrentUtc);
        var seedService = new IdentitySeedService(
            _dbContext,
            passwordHasher,
            Options.Create(new SeedAdminOptions()),
            hostEnvironment,
            timeProvider);

        var staffManagementService = new StaffManagementService(
            _dbContext,
            seedService,
            passwordHasher,
            timeProvider,
            null!,
            null!);

        var availabilityManagementService = new AvailabilityManagementService(_dbContext, timeProvider);
        var appointmentManagementService = new AppointmentManagementService(_dbContext, timeProvider, new NoOpAppointmentNotificationService());

        _adminStaffService = staffManagementService;
        _adminStaffAvailabilityService = availabilityManagementService;
        _staffAppointmentsService = appointmentManagementService;
        _adminAppointmentsService = appointmentManagementService;
        _publicAvailabilityService = new PublicAvailabilityService(_dbContext, timeProvider);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    [Fact]
    public async Task CreateForCurrentStaffAsync_CreatesManualAppointmentForOwnProfile()
    {
        var staff = await CreateStaffAsync("manual-own@example.com", "Manual Own", "Manual Own");
        var nextDay = CurrentDate.AddDays(1);

        await _adminStaffAvailabilityService.ReplaceRulesAsync(staff.StaffProfileId, [Rule(DayOfWeek.Tuesday, 14, 0, 16, 0)]);

        var created = await _staffAppointmentsService.CreateForCurrentStaffAsync(
            staff.UserId,
            new StaffManualAppointmentCreateRequest(
                ToUtc(nextDay, 14, 0),
                null,
                "Walk In",
                "walk-in@example.com",
                "+5491100000010",
                "No beard trim"));

        Assert.Equal(staff.StaffProfileId, created.StaffProfileId);
        Assert.Equal(AppointmentSource.Manual, created.Source);
        Assert.Equal(AppointmentStatus.Confirmed, created.Status);
        Assert.Equal("Walk In", created.CustomerName);

        var slots = await _publicAvailabilityService.GetSlotsAsync(staff.StaffProfileId, nextDay, nextDay);
        Assert.DoesNotContain(slots.Slots, slot => slot.StartAtUtc == ToUtc(nextDay, 14, 0));
    }

    [Fact]
    public async Task UpdateForCurrentStaffAsync_RejectsAppointmentsFromOtherStaff()
    {
        var firstStaff = await CreateStaffAsync("manual-first@example.com", "Manual First", "Manual First");
        var secondStaff = await CreateStaffAsync("manual-second@example.com", "Manual Second", "Manual Second");
        var nextDay = CurrentDate.AddDays(1);

        await _adminStaffAvailabilityService.ReplaceRulesAsync(firstStaff.StaffProfileId, [Rule(DayOfWeek.Tuesday, 9, 0, 12, 0)]);
        await _adminStaffAvailabilityService.ReplaceRulesAsync(secondStaff.StaffProfileId, [Rule(DayOfWeek.Tuesday, 9, 0, 12, 0)]);

        var created = await _adminAppointmentsService.CreateAsync(new AdminManualAppointmentCreateRequest(
            firstStaff.StaffProfileId,
            ToUtc(nextDay, 9, 0),
            ToUtc(nextDay, 9, 30),
            "Customer A",
            "customer-a@example.com",
            null,
            null));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _staffAppointmentsService.UpdateForCurrentStaffAsync(
                secondStaff.UserId,
                created.Id,
                new AppointmentUpdateRequest(
                    ToUtc(nextDay, 9, 30),
                    ToUtc(nextDay, 10, 0),
                    "Customer B",
                    "customer-b@example.com",
                    null,
                    "Reschedule")));
    }

    [Fact]
    public async Task AdminUpdateAsync_AllowsCrossStaffManagement()
    {
        var staff = await CreateStaffAsync("manual-admin@example.com", "Manual Admin", "Manual Admin");
        var nextDay = CurrentDate.AddDays(1);

        await _adminStaffAvailabilityService.ReplaceRulesAsync(staff.StaffProfileId, [Rule(DayOfWeek.Tuesday, 9, 0, 12, 0)]);

        var created = await _adminAppointmentsService.CreateAsync(new AdminManualAppointmentCreateRequest(
            staff.StaffProfileId,
            ToUtc(nextDay, 9, 0),
            ToUtc(nextDay, 9, 30),
            "Original Name",
            "original@example.com",
            "+5491100000020",
            "Original notes"));

        var updated = await _adminAppointmentsService.UpdateAsync(
            created.Id,
            new AppointmentUpdateRequest(
                ToUtc(nextDay, 10, 0),
                ToUtc(nextDay, 10, 30),
                "Updated Name",
                "updated@example.com",
                "+5491100000030",
                "Updated notes"));

        var statusUpdated = await _adminAppointmentsService.UpdateStatusAsync(
            created.Id,
            new AppointmentStatusUpdateRequest(AppointmentStatus.NoShow));

        Assert.Equal("Updated Name", updated.CustomerName);
        Assert.Equal("updated@example.com", updated.CustomerEmail);
        Assert.Equal(ToUtc(nextDay, 10, 0), updated.StartsAtUtc);
        Assert.Equal(AppointmentStatus.NoShow, statusUpdated.Status);
    }

    [Theory]
    [InlineData(AppointmentStatus.Completed)]
    [InlineData(AppointmentStatus.Cancelled)]
    [InlineData(AppointmentStatus.NoShow)]
    public async Task UpdateStatusForCurrentStaffAsync_AppliesTerminalStatus(AppointmentStatus targetStatus)
    {
        var staff = await CreateStaffAsync($"status-{targetStatus}@example.com", "Status Staff", "Status Staff");
        var nextDay = CurrentDate.AddDays(1);

        await _adminStaffAvailabilityService.ReplaceRulesAsync(staff.StaffProfileId, [Rule(DayOfWeek.Tuesday, 9, 0, 12, 0)]);

        var created = await _staffAppointmentsService.CreateForCurrentStaffAsync(
            staff.UserId,
            new StaffManualAppointmentCreateRequest(
                ToUtc(nextDay, 9, 0),
                ToUtc(nextDay, 9, 30),
                "Status Customer",
                null,
                null,
                null));

        var updated = await _staffAppointmentsService.UpdateStatusForCurrentStaffAsync(
            staff.UserId,
            created.Id,
            new AppointmentStatusUpdateRequest(targetStatus));

        Assert.Equal(targetStatus, updated.Status);
    }

    [Fact]
    public async Task ManualAppointment_PreservesCustomerSnapshot_AndCancelledStatusReleasesSlot()
    {
        var staff = await CreateStaffAsync("snapshot-staff@example.com", "Snapshot Staff", "Snapshot Staff");
        var nextDay = CurrentDate.AddDays(1);

        await _adminStaffAvailabilityService.ReplaceRulesAsync(staff.StaffProfileId, [Rule(DayOfWeek.Tuesday, 9, 0, 10, 0)]);

        var created = await _staffAppointmentsService.CreateForCurrentStaffAsync(
            staff.UserId,
            new StaffManualAppointmentCreateRequest(
                ToUtc(nextDay, 9, 0),
                ToUtc(nextDay, 9, 30),
                "Snapshot Customer",
                "snapshot@example.com",
                "+5491100000040",
                "Keep this snapshot"));

        Assert.Equal("Snapshot Customer", created.CustomerName);
        Assert.Equal("snapshot@example.com", created.CustomerEmail);
        Assert.Equal("+5491100000040", created.CustomerPhone);

        var beforeCancel = await _publicAvailabilityService.GetSlotsAsync(staff.StaffProfileId, nextDay, nextDay);
        Assert.DoesNotContain(beforeCancel.Slots, slot => slot.StartAtUtc == ToUtc(nextDay, 9, 0));

        await _staffAppointmentsService.UpdateStatusForCurrentStaffAsync(
            staff.UserId,
            created.Id,
            new AppointmentStatusUpdateRequest(AppointmentStatus.Cancelled));

        var afterCancel = await _publicAvailabilityService.GetSlotsAsync(staff.StaffProfileId, nextDay, nextDay);
        Assert.Contains(afterCancel.Slots, slot => slot.StartAtUtc == ToUtc(nextDay, 9, 0));
    }

    private static AvailabilityRuleRequest Rule(DayOfWeek dayOfWeek, int startHour, int startMinute, int endHour, int endMinute)
        => new((int)dayOfWeek, new TimeOnly(startHour, startMinute), new TimeOnly(endHour, endMinute), true);

    private async Task<StaffManagementView> CreateStaffAsync(string email, string fullName, string displayName)
        => await _adminStaffService.CreateAsync(new AdminStaffCreateRequest(
            fullName,
            email,
            displayName,
            "Secret123!",
            "+5491100000000",
            null,
            null,
            null,
            null,
            true));

    private static DateOnly CurrentDate => DateOnly.FromDateTime(CurrentUtc.UtcDateTime);

    private static DateTime ToUtc(DateOnly date, int hour, int minute)
        => BogotaClock.ToUtc(date, new TimeOnly(hour, minute));

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "Barbershop.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(AppContext.BaseDirectory);
    }

    private sealed class NoOpAppointmentNotificationService : IAppointmentNotificationService
    {
        public Task NotifyStaffOfNewAppointmentAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task NotifyStaffOfCustomerCancellationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task NotifyCustomerOfAppointmentUpdateAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task NotifyCustomerOfAppointmentCancellationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task NotifyCustomerOfAppointmentConfirmationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
