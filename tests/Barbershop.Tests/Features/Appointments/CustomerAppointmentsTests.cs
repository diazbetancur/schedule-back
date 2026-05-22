using Barbershop.Application.Appointments;
using Barbershop.Application.Availability;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Staff;
using Barbershop.Application.Staff.Admin;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Users;
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

public sealed class CustomerAppointmentsTests : IDisposable
{
    private static readonly DateTimeOffset CurrentUtc = new(2026, 1, 5, 10, 15, 0, TimeSpan.Zero);

    private readonly AppDbContext _dbContext;
    private readonly IPasswordHasher<object> _passwordHasher;
    private readonly IdentitySeedService _seedService;
    private readonly IAdminStaffService _adminStaffService;
    private readonly IAdminStaffAvailabilityService _adminStaffAvailabilityService;
    private readonly ICustomerAppointmentsService _customerAppointmentsService;
    private readonly IPublicAvailabilityService _publicAvailabilityService;

    public CustomerAppointmentsTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        _dbContext = new AppDbContext(options);

        _passwordHasher = new PasswordHasher<object>();
        var hostEnvironment = new TestHostEnvironment();
        var timeProvider = new FixedTimeProvider(CurrentUtc);
        _seedService = new IdentitySeedService(
            _dbContext,
            _passwordHasher,
            Options.Create(new SeedAdminOptions()),
            hostEnvironment,
            timeProvider);

        var staffManagementService = new StaffManagementService(
            _dbContext,
            _seedService,
            _passwordHasher,
            timeProvider,
            null!,
            null!);

        var availabilityManagementService = new AvailabilityManagementService(_dbContext, timeProvider);
        var appointmentManagementService = new AppointmentManagementService(_dbContext, timeProvider);

        _adminStaffService = staffManagementService;
        _adminStaffAvailabilityService = availabilityManagementService;
        _customerAppointmentsService = appointmentManagementService;
        _publicAvailabilityService = new PublicAvailabilityService(_dbContext, timeProvider);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    [Fact]
    public async Task CreateAsync_BooksAvailableSlot_ForCurrentCustomer()
    {
        var staff = await CreateStaffAsync("book-staff@example.com", "Book Staff", "Book Staff");
        var customer = await CreateCustomerAsync("book-customer@example.com", "Book Customer", "+5491100000001");
        var nextDay = CurrentDate.AddDays(1);

        await _adminStaffAvailabilityService.ReplaceRulesAsync(staff.StaffProfileId, [Rule(DayOfWeek.Tuesday, 9, 0, 11, 0)]);

        var created = await _customerAppointmentsService.CreateAsync(
            customer.Id,
            new CustomerAppointmentCreateRequest(staff.StaffProfileId, ToUtc(nextDay, 9, 0), "Window seat"));

        Assert.Equal(staff.StaffProfileId, created.StaffProfileId);
        Assert.Equal(customer.Id, created.CustomerUserId);
        Assert.Equal(AppointmentStatus.Pending, created.Status);
        Assert.Equal(AppointmentSource.CustomerBooking, created.Source);
        Assert.Equal("Book Customer", created.CustomerName);
        Assert.Equal("book-customer@example.com", created.CustomerEmail);
        Assert.Equal("+5491100000001", created.CustomerPhone);

        var slots = await _publicAvailabilityService.GetSlotsAsync(staff.StaffProfileId, nextDay, nextDay);
        Assert.DoesNotContain(slots.Slots, slot => slot.StartAtUtc == ToUtc(nextDay, 9, 0));
    }

    [Fact]
    public async Task CreateAsync_RejectsPastSlots()
    {
        var staff = await CreateStaffAsync("past-staff@example.com", "Past Staff", "Past Staff");
        var customer = await CreateCustomerAsync("past-customer@example.com", "Past Customer", null);

        await _adminStaffAvailabilityService.ReplaceRulesAsync(staff.StaffProfileId, [Rule(DayOfWeek.Monday, 9, 0, 12, 0)]);

        var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
            _customerAppointmentsService.CreateAsync(
                customer.Id,
                new CustomerAppointmentCreateRequest(staff.StaffProfileId, ToUtc(CurrentDate, 10, 0), null)));

        Assert.Contains("startsAtUtc", exception.Errors.Keys);
    }

    [Fact]
    public async Task CreateAsync_RejectsUnavailableAndOverlappingSlots()
    {
        var staff = await CreateStaffAsync("conflict-staff@example.com", "Conflict Staff", "Conflict Staff");
        var firstCustomer = await CreateCustomerAsync("conflict-customer-1@example.com", "Conflict Customer 1", null);
        var secondCustomer = await CreateCustomerAsync("conflict-customer-2@example.com", "Conflict Customer 2", null);
        var nextDay = CurrentDate.AddDays(1);

        await _adminStaffAvailabilityService.ReplaceRulesAsync(staff.StaffProfileId, [Rule(DayOfWeek.Tuesday, 9, 0, 11, 0)]);
        await _adminStaffAvailabilityService.CreateUnavailablePeriodAsync(
            staff.StaffProfileId,
            new UnavailablePeriodCreateRequest(ToUtc(nextDay, 9, 0), ToUtc(nextDay, 9, 30), "Blocked"));

        await Assert.ThrowsAsync<ConflictException>(() =>
            _customerAppointmentsService.CreateAsync(
                firstCustomer.Id,
                new CustomerAppointmentCreateRequest(staff.StaffProfileId, ToUtc(nextDay, 9, 0), null)));

        await _customerAppointmentsService.CreateAsync(
            firstCustomer.Id,
            new CustomerAppointmentCreateRequest(staff.StaffProfileId, ToUtc(nextDay, 9, 30), null));

        await Assert.ThrowsAsync<ConflictException>(() =>
            _customerAppointmentsService.CreateAsync(
                secondCustomer.Id,
                new CustomerAppointmentCreateRequest(staff.StaffProfileId, ToUtc(nextDay, 9, 30), null)));
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsOnlyCurrentCustomerAppointments()
    {
        var staff = await CreateStaffAsync("history-staff@example.com", "History Staff", "History Staff");
        var firstCustomer = await CreateCustomerAsync("history-customer-1@example.com", "History Customer 1", null);
        var secondCustomer = await CreateCustomerAsync("history-customer-2@example.com", "History Customer 2", null);
        var nextDay = CurrentDate.AddDays(1);

        await _adminStaffAvailabilityService.ReplaceRulesAsync(staff.StaffProfileId, [Rule(DayOfWeek.Tuesday, 9, 0, 11, 0)]);

        await _customerAppointmentsService.CreateAsync(
            firstCustomer.Id,
            new CustomerAppointmentCreateRequest(staff.StaffProfileId, ToUtc(nextDay, 9, 0), null));

        await _customerAppointmentsService.CreateAsync(
            firstCustomer.Id,
            new CustomerAppointmentCreateRequest(staff.StaffProfileId, ToUtc(nextDay, 9, 30), null));

        await _customerAppointmentsService.CreateAsync(
            secondCustomer.Id,
            new CustomerAppointmentCreateRequest(staff.StaffProfileId, ToUtc(nextDay, 10, 0), null));

        var history = await _customerAppointmentsService.GetHistoryAsync(firstCustomer.Id);

        Assert.Equal(2, history.Count);
        Assert.All(history, item => Assert.Equal(firstCustomer.Id, item.CustomerUserId));
    }

    [Fact]
    public async Task CancelAsync_RejectsAppointmentsOwnedByAnotherCustomer()
    {
        var staff = await CreateStaffAsync("cancel-other-staff@example.com", "Cancel Other Staff", "Cancel Other Staff");
        var owner = await CreateCustomerAsync("cancel-owner@example.com", "Cancel Owner", null);
        var intruder = await CreateCustomerAsync("cancel-intruder@example.com", "Cancel Intruder", null);
        var nextDay = CurrentDate.AddDays(1);

        await _adminStaffAvailabilityService.ReplaceRulesAsync(staff.StaffProfileId, [Rule(DayOfWeek.Tuesday, 9, 0, 10, 0)]);

        var appointment = await _customerAppointmentsService.CreateAsync(
            owner.Id,
            new CustomerAppointmentCreateRequest(staff.StaffProfileId, ToUtc(nextDay, 9, 0), null));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _customerAppointmentsService.CancelAsync(intruder.Id, appointment.Id));
    }

    [Fact]
    public async Task CancelAsync_CancelledAppointmentDoesNotBlockPublicAvailability()
    {
        var staff = await CreateStaffAsync("cancel-release-staff@example.com", "Cancel Release Staff", "Cancel Release Staff");
        var customer = await CreateCustomerAsync("cancel-release-customer@example.com", "Cancel Release Customer", null);
        var nextDay = CurrentDate.AddDays(1);

        await _adminStaffAvailabilityService.ReplaceRulesAsync(staff.StaffProfileId, [Rule(DayOfWeek.Tuesday, 9, 0, 10, 0)]);

        var appointment = await _customerAppointmentsService.CreateAsync(
            customer.Id,
            new CustomerAppointmentCreateRequest(staff.StaffProfileId, ToUtc(nextDay, 9, 0), null));

        var beforeCancel = await _publicAvailabilityService.GetSlotsAsync(staff.StaffProfileId, nextDay, nextDay);
        Assert.DoesNotContain(beforeCancel.Slots, slot => slot.StartAtUtc == ToUtc(nextDay, 9, 0));

        var cancelled = await _customerAppointmentsService.CancelAsync(customer.Id, appointment.Id);

        Assert.Equal(AppointmentStatus.Cancelled, cancelled.Status);

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

    private async Task<User> CreateCustomerAsync(string email, string fullName, string? phoneNumber)
    {
        await _seedService.EnsureSeededAsync();

        var customerRole = await _dbContext.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Customer.ToUpperInvariant());
        var createdAt = CurrentUtc.UtcDateTime;

        var user = new User(
            fullName,
            email,
            _passwordHasher.HashPassword(new object(), "Secret123!"),
            createdAt,
            phoneNumber);

        user.UserRoles.Add(new UserRole(user.Id, customerRole.Id, createdAt));

        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        return user;
    }

    private static DateOnly CurrentDate => DateOnly.FromDateTime(CurrentUtc.UtcDateTime);

    private static DateTime ToUtc(DateOnly date, int hour, int minute)
        => DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(hour, minute)), DateTimeKind.Utc);

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
}
