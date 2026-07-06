using Barbershop.Application.Appointments;
using Barbershop.Application.Availability;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Notifications;
using Barbershop.Application.Reviews;
using Barbershop.Application.Staff;
using Barbershop.Application.Staff.Admin;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Appointments;
using Barbershop.Infrastructure.Availability;
using Barbershop.Infrastructure.Configuration;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Reviews;
using Barbershop.Infrastructure.Staff;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Barbershop.Tests.Features.Reviews;

public sealed class ReviewEligibilityTests : IDisposable
{
  private static readonly DateTimeOffset CurrentUtc = new(2026, 1, 5, 10, 15, 0, TimeSpan.Zero);

  private readonly AppDbContext _dbContext;
  private readonly IPasswordHasher<object> _passwordHasher;
  private readonly IdentitySeedService _seedService;
  private readonly IAdminStaffService _adminStaffService;
  private readonly IAdminStaffAvailabilityService _adminStaffAvailabilityService;
  private readonly ICustomerAppointmentsService _customerAppointmentsService;
  private readonly IAdminAppointmentsService _adminAppointmentsService;
  private readonly ICustomerReviewsService _customerReviewsService;
  private readonly IPublicReviewsService _publicReviewsService;

  public ReviewEligibilityTests()
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
    var appointmentManagementService = new AppointmentManagementService(_dbContext, timeProvider, new NoOpAppointmentNotificationService());
    var reviewManagementService = new ReviewManagementService(_dbContext, timeProvider);

    _adminStaffService = staffManagementService;
    _adminStaffAvailabilityService = availabilityManagementService;
    _customerAppointmentsService = appointmentManagementService;
    _adminAppointmentsService = appointmentManagementService;
    _customerReviewsService = reviewManagementService;
    _publicReviewsService = reviewManagementService;
  }

  public void Dispose()
  {
    _dbContext.Dispose();
  }

  [Fact]
  public async Task CreateAsync_CreatesReview_ForOwnedCompletedAppointment()
  {
    var staff = await CreateStaffAsync("review-owned-staff@example.com", "Review Owned Staff", "Review Owned Staff");
    var customer = await CreateCustomerAsync("review-owned-customer@example.com", "Review Owned Customer", null);
    var nextDay = CurrentDate.AddDays(1);

    await _adminStaffAvailabilityService.ReplaceRulesAsync(staff.StaffProfileId, [Rule(DayOfWeek.Tuesday, 9, 0, 11, 0)]);

    var appointment = await CreateCustomerAppointmentAsync(customer.Id, staff.StaffProfileId, nextDay, 9, 0);
    await CompleteAppointmentAsync(appointment.Id);

    var review = await _customerReviewsService.CreateAsync(
        customer.Id,
        appointment.Id,
        new ReviewCreateRequest(5, "Great service"));

    Assert.Equal(appointment.Id, review.AppointmentId);
    Assert.Equal(staff.StaffProfileId, review.StaffProfileId);
    Assert.Equal(5, review.Stars);
    Assert.Equal("Great service", review.Comment);

    var customerReviews = await _customerReviewsService.GetByCurrentCustomerAsync(customer.Id);
    Assert.Single(customerReviews);
  }

  [Fact]
  public async Task CreateAsync_RejectsAppointmentThatIsNotCompleted()
  {
    var staff = await CreateStaffAsync("review-pending-staff@example.com", "Review Pending Staff", "Review Pending Staff");
    var customer = await CreateCustomerAsync("review-pending-customer@example.com", "Review Pending Customer", null);
    var nextDay = CurrentDate.AddDays(1);

    await _adminStaffAvailabilityService.ReplaceRulesAsync(staff.StaffProfileId, [Rule(DayOfWeek.Tuesday, 9, 0, 11, 0)]);

    var appointment = await CreateCustomerAppointmentAsync(customer.Id, staff.StaffProfileId, nextDay, 9, 0);

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _customerReviewsService.CreateAsync(customer.Id, appointment.Id, new ReviewCreateRequest(4, "Too soon")));

    Assert.Contains("appointmentId", exception.Errors.Keys);
  }

  [Fact]
  public async Task CreateAsync_CreatesReview_ForPastPendingAppointment()
  {
    // Appointment was never manually marked Completed but its end time has passed.
    var staff = await CreateStaffAsync("review-past-staff@example.com", "Review Past Staff", "Review Past Staff");
    var customer = await CreateCustomerAsync("review-past-customer@example.com", "Review Past Customer", null);

    // Seed a past appointment directly — bypass the future-time guard in the booking endpoint.
    var pastStart = ToUtc(CurrentDate, 1, 0); // 01:00 on current day, before 10:15 (CurrentUtc)
    var entity = new Appointment(
        staff.StaffProfileId,
        customer.FullName,
        pastStart,
        pastStart.AddMinutes(30),
        AppointmentStatus.Pending,
        AppointmentSource.CustomerBooking,
        CurrentUtc.AddHours(-10).UtcDateTime,
        customer.Id,
        customer.Email,
        null,
        null);

    _dbContext.Appointments.Add(entity);
    await _dbContext.SaveChangesAsync();

    var review = await _customerReviewsService.CreateAsync(
        customer.Id,
        entity.Id,
        new ReviewCreateRequest(4, "Good cut"));

    Assert.Equal(entity.Id, review.AppointmentId);
    Assert.Equal(4, review.Stars);

    // Appointment should be auto-completed after review creation.
    var updated = await _dbContext.Appointments.FindAsync(entity.Id);
    Assert.Equal(AppointmentStatus.Completed, updated!.Status);
  }

  [Fact]
  public async Task CreateAsync_RejectsAppointmentOwnedByAnotherCustomer()
  {
    var staff = await CreateStaffAsync("review-owner-staff@example.com", "Review Owner Staff", "Review Owner Staff");
    var owner = await CreateCustomerAsync("review-owner-customer@example.com", "Review Owner Customer", null);
    var intruder = await CreateCustomerAsync("review-intruder-customer@example.com", "Review Intruder Customer", null);
    var nextDay = CurrentDate.AddDays(1);

    await _adminStaffAvailabilityService.ReplaceRulesAsync(staff.StaffProfileId, [Rule(DayOfWeek.Tuesday, 9, 0, 11, 0)]);

    var appointment = await CreateCustomerAppointmentAsync(owner.Id, staff.StaffProfileId, nextDay, 9, 0);
    await CompleteAppointmentAsync(appointment.Id);

    await Assert.ThrowsAsync<KeyNotFoundException>(() =>
        _customerReviewsService.CreateAsync(intruder.Id, appointment.Id, new ReviewCreateRequest(4, "Not mine")));
  }

  [Fact]
  public async Task CreateAsync_RejectsDuplicateReview()
  {
    var staff = await CreateStaffAsync("review-duplicate-staff@example.com", "Review Duplicate Staff", "Review Duplicate Staff");
    var customer = await CreateCustomerAsync("review-duplicate-customer@example.com", "Review Duplicate Customer", null);
    var nextDay = CurrentDate.AddDays(1);

    await _adminStaffAvailabilityService.ReplaceRulesAsync(staff.StaffProfileId, [Rule(DayOfWeek.Tuesday, 9, 0, 11, 0)]);

    var appointment = await CreateCustomerAppointmentAsync(customer.Id, staff.StaffProfileId, nextDay, 9, 0);
    await CompleteAppointmentAsync(appointment.Id);

    await _customerReviewsService.CreateAsync(customer.Id, appointment.Id, new ReviewCreateRequest(5, "First"));

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _customerReviewsService.CreateAsync(customer.Id, appointment.Id, new ReviewCreateRequest(3, "Second")));

    Assert.Contains("appointmentId", exception.Errors.Keys);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(6)]
  public async Task CreateAsync_RejectsStarsOutsideAllowedRange(int stars)
  {
    var staff = await CreateStaffAsync($"review-stars-{stars}-staff@example.com", "Review Stars Staff", "Review Stars Staff");
    var customer = await CreateCustomerAsync($"review-stars-{stars}-customer@example.com", "Review Stars Customer", null);
    var nextDay = CurrentDate.AddDays(1);

    await _adminStaffAvailabilityService.ReplaceRulesAsync(staff.StaffProfileId, [Rule(DayOfWeek.Tuesday, 9, 0, 11, 0)]);

    var appointment = await CreateCustomerAppointmentAsync(customer.Id, staff.StaffProfileId, nextDay, 9, 0);
    await CompleteAppointmentAsync(appointment.Id);

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _customerReviewsService.CreateAsync(customer.Id, appointment.Id, new ReviewCreateRequest(stars, "Invalid stars")));

    Assert.Contains("stars", exception.Errors.Keys);
  }

  [Fact]
  public async Task GetSummaryByStaffProfileAsync_ReturnsAverageAndCount()
  {
    var staff = await CreateStaffAsync("review-summary-staff@example.com", "Review Summary Staff", "Review Summary Staff");
    var firstCustomer = await CreateCustomerAsync("review-summary-customer-1@example.com", "Review Summary Customer 1", null);
    var secondCustomer = await CreateCustomerAsync("review-summary-customer-2@example.com", "Review Summary Customer 2", null);
    var nextDay = CurrentDate.AddDays(1);

    await _adminStaffAvailabilityService.ReplaceRulesAsync(staff.StaffProfileId, [Rule(DayOfWeek.Tuesday, 9, 0, 12, 0)]);

    var firstAppointment = await CreateCustomerAppointmentAsync(firstCustomer.Id, staff.StaffProfileId, nextDay, 9, 0);
    await CompleteAppointmentAsync(firstAppointment.Id);
    await _customerReviewsService.CreateAsync(firstCustomer.Id, firstAppointment.Id, new ReviewCreateRequest(4, "Great"));

    var secondAppointment = await CreateCustomerAppointmentAsync(secondCustomer.Id, staff.StaffProfileId, nextDay, 10, 0);
    await CompleteAppointmentAsync(secondAppointment.Id);
    await _customerReviewsService.CreateAsync(secondCustomer.Id, secondAppointment.Id, new ReviewCreateRequest(5, "Excellent"));

    var summary = await _publicReviewsService.GetSummaryByStaffProfileAsync(staff.StaffProfileId);
    var reviews = await _publicReviewsService.GetByStaffProfileAsync(staff.StaffProfileId);

    Assert.Equal(2, summary.TotalReviews);
    Assert.Equal(4.5m, summary.AverageStars);
    Assert.Equal(2, reviews.Count);
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
          30,
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

  private async Task<AppointmentView> CreateCustomerAppointmentAsync(Guid customerUserId, Guid staffProfileId, DateOnly date, int hour, int minute)
      => await _customerAppointmentsService.CreateAsync(
          customerUserId,
          new CustomerAppointmentCreateRequest(staffProfileId, ToUtc(date, hour, minute), null));

  private async Task CompleteAppointmentAsync(Guid appointmentId)
  {
    await _adminAppointmentsService.UpdateStatusAsync(
        appointmentId,
        new AppointmentStatusUpdateRequest(AppointmentStatus.Completed));
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

  private sealed class NoOpAppointmentNotificationService : IAppointmentNotificationService
  {
    public Task NotifyStaffOfNewAppointmentAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NotifyStaffOfCustomerCancellationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NotifyCustomerOfAppointmentUpdateAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NotifyCustomerOfAppointmentCancellationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NotifyCustomerOfAppointmentConfirmationAsync(AppointmentNotificationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }
}
