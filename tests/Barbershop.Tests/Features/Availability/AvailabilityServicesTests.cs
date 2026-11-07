using Barbershop.Application.Availability;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Staff;
using Barbershop.Application.Staff.Admin;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Common;
using Barbershop.Infrastructure.Availability;
using Barbershop.Infrastructure.Configuration;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Staff;
using Microsoft.AspNetCore.Identity;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Barbershop.Tests.Features.Availability;

public sealed class AvailabilityServicesTests : IDisposable
{
  // 10:15 AM Bogota local time -> 15:15 UTC (Bogota is UTC-5).
  private static readonly DateTimeOffset CurrentUtc = new(2026, 1, 5, 15, 15, 0, TimeSpan.Zero);

  private readonly AppDbContext _dbContext;
  private readonly IAdminStaffService _adminStaffService;
  private readonly IStaffAvailabilityService _staffAvailabilityService;
  private readonly IAdminStaffAvailabilityService _adminStaffAvailabilityService;
  private readonly IPublicAvailabilityService _publicAvailabilityService;

  public AvailabilityServicesTests()
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

    _adminStaffService = staffManagementService;
    _staffAvailabilityService = availabilityManagementService;
    _adminStaffAvailabilityService = availabilityManagementService;
    _publicAvailabilityService = new PublicAvailabilityService(_dbContext, timeProvider);
  }

  public void Dispose()
  {
    _dbContext.Dispose();
  }

  [Fact]
  public async Task ReplaceRulesAsync_ReplacesCurrentStaffRules()
  {
    var staff = await CreateStaffAsync("replace-rules@example.com", "Replace Rules", "Replace Rules");

    await _staffAvailabilityService.ReplaceRulesAsync(staff.UserId, [Rule(DayOfWeek.Monday, 9, 0, 11, 0)]);

    var updated = await _staffAvailabilityService.ReplaceRulesAsync(
        staff.UserId,
        [
            Rule(DayOfWeek.Tuesday, 10, 0, 12, 0),
                Rule(DayOfWeek.Tuesday, 13, 0, 15, 0)
        ]);

    Assert.Collection(
        updated,
        first =>
        {
          Assert.Equal((int)DayOfWeek.Tuesday, first.DayOfWeek);
          Assert.Equal(new TimeOnly(10, 0), first.StartTime);
          Assert.Equal(new TimeOnly(12, 0), first.EndTime);
        },
        second =>
        {
          Assert.Equal((int)DayOfWeek.Tuesday, second.DayOfWeek);
          Assert.Equal(new TimeOnly(13, 0), second.StartTime);
          Assert.Equal(new TimeOnly(15, 0), second.EndTime);
        });

    var storedRules = await _dbContext.StaffAvailabilityRules
        .Where(rule => rule.StaffProfileId == staff.StaffProfileId)
        .OrderBy(rule => rule.DayOfWeek)
        .ThenBy(rule => rule.StartTime)
        .ToListAsync();

    Assert.Equal(2, storedRules.Count);
    Assert.DoesNotContain(storedRules, rule => rule.DayOfWeek == DayOfWeek.Monday);

    var availability = await _staffAvailabilityService.GetCurrentAsync(staff.UserId);
    Assert.Equal(2, availability.Rules.Count);
  }

  [Fact]
  public async Task ReplaceRulesAsync_RejectsInvalidTimeRange()
  {
    var staff = await CreateStaffAsync("invalid-rule@example.com", "Invalid Rule", "Invalid Rule");

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _staffAvailabilityService.ReplaceRulesAsync(
            staff.UserId,
            [new AvailabilityRuleRequest((int)DayOfWeek.Monday, new TimeOnly(17, 0), new TimeOnly(9, 0))]));

    Assert.Contains("rules[0].endTime", exception.Errors.Keys);
  }

  [Fact]
  public async Task ReplaceRulesAsync_RejectsOverlappingActiveRules()
  {
    var staff = await CreateStaffAsync("overlap-rule@example.com", "Overlap Rule", "Overlap Rule");

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _staffAvailabilityService.ReplaceRulesAsync(
            staff.UserId,
            [
                Rule(DayOfWeek.Wednesday, 9, 0, 12, 0),
                    Rule(DayOfWeek.Wednesday, 11, 30, 14, 0)
            ]));

    Assert.Contains("rules", exception.Errors.Keys);
  }

  [Fact]
  public async Task CreateUnavailablePeriodAsync_CreatesPeriodForCurrentStaff()
  {
    var staff = await CreateStaffAsync("period-create@example.com", "Period Create", "Period Create");
    var nextDay = CurrentDate.AddDays(1);

    var created = await _staffAvailabilityService.CreateUnavailablePeriodAsync(
        staff.UserId,
        new UnavailablePeriodCreateRequest(
            ToUtc(nextDay, 14, 0),
            ToUtc(nextDay, 16, 0),
            "Medical leave"));

    Assert.Equal(ToUtc(nextDay, 14, 0), created.StartsAtUtc);
    Assert.Equal(ToUtc(nextDay, 16, 0), created.EndsAtUtc);
    Assert.Equal("Medical leave", created.Reason);

    var stored = await _dbContext.StaffUnavailablePeriods.SingleAsync(period => period.StaffProfileId == staff.StaffProfileId);
    Assert.Equal(created.Id, stored.Id);
    Assert.Equal("Medical leave", stored.Reason);
  }

  [Fact]
  public async Task CreateUnavailablePeriodAsync_RejectsInvalidRange()
  {
    var staff = await CreateStaffAsync("period-invalid@example.com", "Period Invalid", "Period Invalid");
    var nextDay = CurrentDate.AddDays(1);

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _staffAvailabilityService.CreateUnavailablePeriodAsync(
            staff.UserId,
            new UnavailablePeriodCreateRequest(
                ToUtc(nextDay, 16, 0),
                ToUtc(nextDay, 14, 0),
                "Invalid")));

    Assert.Contains("endsAtUtc", exception.Errors.Keys);
  }

  [Fact]
  public async Task GetSlotsAsync_GeneratesFutureSlotsFromActiveRules()
  {
    var staff = await CreateStaffAsync("slot-generation@example.com", "Slot Generation", "Slot Generation");
    var nextDay = CurrentDate.AddDays(1);

    await _adminStaffAvailabilityService.ReplaceRulesAsync(
        staff.StaffProfileId,
        [
            Rule(DayOfWeek.Tuesday, 9, 0, 10, 30),
                Rule(DayOfWeek.Tuesday, 15, 0, 16, 0, false)
        ]);

    var response = await _publicAvailabilityService.GetSlotsAsync(staff.StaffProfileId, nextDay, nextDay, Corte, null);

    // F1: a slot every 15 minutes from the start of the rule, as long as the 30-minute service fits.
    Assert.Equal(30, response.SlotDurationMinutes);
    Assert.Equal(15, response.SlotStepMinutes);
    Assert.Equal(Corte, response.GeneralServiceId);
    AssertStarts(response, nextDay, (9, 0), (9, 15), (9, 30), (9, 45), (10, 0));
    Assert.All(response.Slots, slot => Assert.Equal(TimeSpan.FromMinutes(30), slot.EndAtUtc - slot.StartAtUtc));
  }

  [Fact]
  public async Task GetSlotsAsync_ExcludesUnavailablePeriods()
  {
    var staff = await CreateStaffAsync("slot-unavailable@example.com", "Slot Unavailable", "Slot Unavailable");
    var nextDay = CurrentDate.AddDays(1);

    await _staffAvailabilityService.ReplaceRulesAsync(staff.UserId, [Rule(DayOfWeek.Tuesday, 9, 0, 11, 0)]);
    await _staffAvailabilityService.CreateUnavailablePeriodAsync(
        staff.UserId,
        new UnavailablePeriodCreateRequest(
            ToUtc(nextDay, 9, 30),
            ToUtc(nextDay, 10, 30),
            "Break"));

    var response = await _publicAvailabilityService.GetSlotsAsync(staff.StaffProfileId, nextDay, nextDay, Corte, null);

    AssertStarts(response, nextDay, (9, 0), (10, 30));
  }

  [Fact]
  public async Task GetSlotsAsync_ExcludesPendingAndConfirmedAppointments()
  {
    var staff = await CreateStaffAsync("slot-appointments@example.com", "Slot Appointments", "Slot Appointments");
    var nextDay = CurrentDate.AddDays(1);

    await _staffAvailabilityService.ReplaceRulesAsync(staff.UserId, [Rule(DayOfWeek.Tuesday, 9, 0, 11, 0)]);
    await AddAppointmentAsync(staff.StaffProfileId, nextDay, 9, 30, 10, 0, AppointmentStatus.Pending);
    await AddAppointmentAsync(staff.StaffProfileId, nextDay, 10, 0, 10, 30, AppointmentStatus.Confirmed);
    await AddAppointmentAsync(staff.StaffProfileId, nextDay, 10, 30, 11, 0, AppointmentStatus.Completed);

    // Appointments from before services (no reserved duration) block their own EndsAt − StartsAt.
    var response = await _publicAvailabilityService.GetSlotsAsync(staff.StaffProfileId, nextDay, nextDay, Corte, null);

    AssertStarts(response, nextDay, (9, 0), (10, 30));
  }

  [Fact]
  public async Task GetSlotsAsync_ExcludesPastTimesOnCurrentDay()
  {
    var staff = await CreateStaffAsync("slot-past@example.com", "Slot Past", "Slot Past");

    await _staffAvailabilityService.ReplaceRulesAsync(staff.UserId, [Rule(DayOfWeek.Monday, 9, 0, 12, 0)]);

    var response = await _publicAvailabilityService.GetSlotsAsync(staff.StaffProfileId, CurrentDate, CurrentDate, Corte, null);

    // Now is 10:15 Bogotá: 10:15 itself is still offered, nothing before it.
    AssertStarts(response, CurrentDate, (10, 15), (10, 30), (10, 45), (11, 0), (11, 15), (11, 30));
  }

  [Fact]
  public async Task GetSlotsAsync_ConvertsLocalScheduleToUtcWithBogotaOffset()
  {
    var staff = await CreateStaffAsync("slot-offset@example.com", "Slot Offset", "Slot Offset");
    var nextDay = CurrentDate.AddDays(1);

    await _staffAvailabilityService.ReplaceRulesAsync(staff.UserId, [Rule(nextDay.DayOfWeek, 9, 0, 9, 30)]);

    var response = await _publicAvailabilityService.GetSlotsAsync(staff.StaffProfileId, nextDay, nextDay, Corte, null);

    // Bogota (UTC-5) 09:00 local must be stored as 14:00 UTC, not 09:00 UTC.
    var expectedStartUtc = new DateTime(nextDay.Year, nextDay.Month, nextDay.Day, 14, 0, 0, DateTimeKind.Utc);
    var expectedEndUtc = new DateTime(nextDay.Year, nextDay.Month, nextDay.Day, 14, 30, 0, DateTimeKind.Utc);

    var slot = Assert.Single(response.Slots);
    Assert.Equal(expectedStartUtc, slot.StartAtUtc);
    Assert.Equal(expectedEndUtc, slot.EndAtUtc);
  }

  [Fact]
  public async Task GetSlotsAsync_RejectsRangesLongerThanThirtyOneDays()
  {
    var staff = await CreateStaffAsync("slot-range@example.com", "Slot Range", "Slot Range");

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _publicAvailabilityService.GetSlotsAsync(staff.StaffProfileId, CurrentDate, CurrentDate.AddDays(31), Corte, null));

    Assert.Contains("to", exception.Errors.Keys);
  }

  [Fact]
  public async Task AvailabilityChanges_DoNotModifyExistingAppointments()
  {
    var staff = await CreateStaffAsync("appointments-preserved@example.com", "Appointments Preserved", "Appointments Preserved");
    var nextDay = CurrentDate.AddDays(1);

    var appointment = await AddAppointmentAsync(staff.StaffProfileId, nextDay, 9, 0, 9, 30, AppointmentStatus.Pending);

    await _staffAvailabilityService.ReplaceRulesAsync(staff.UserId, [Rule(DayOfWeek.Tuesday, 10, 0, 12, 0)]);
    await _staffAvailabilityService.CreateUnavailablePeriodAsync(
        staff.UserId,
        new UnavailablePeriodCreateRequest(
            ToUtc(nextDay, 9, 0),
            ToUtc(nextDay, 10, 0),
            "Personal time"));

    var stored = await _dbContext.Appointments.SingleAsync(candidate => candidate.Id == appointment.Id);

    Assert.Equal(appointment.Id, stored.Id);
    Assert.Equal(AppointmentStatus.Pending, stored.Status);
    Assert.Equal(AppointmentSource.CustomerBooking, stored.Source);
    Assert.Equal(ToUtc(nextDay, 9, 0), stored.StartsAt);
    Assert.Equal(ToUtc(nextDay, 9, 30), stored.EndsAt);
  }

  [Fact]
  public async Task GetSlotsAsync_UsesTheDurationOfEachService_InRulesThatAreNotMultiples()
  {
    var staff = await CreateStaffAsync("slot-durations@example.com", "Slot Durations", "Slot Durations");
    var nextDay = CurrentDate.AddDays(1);
    var color = await ServiceOfferingTestData.AddGeneralServiceAsync(_dbContext, "Color");
    await ServiceOfferingTestData.OfferAsync(_dbContext, staff.StaffProfileId, color.Id, 45);

    // 9:00-10:10 is not a multiple of 15 nor of 45; a 45-minute service ends at 10:00 at the latest.
    await _staffAvailabilityService.ReplaceRulesAsync(staff.UserId, [Rule(DayOfWeek.Tuesday, 9, 0, 10, 10)]);

    var colorSlots = await _publicAvailabilityService.GetSlotsAsync(staff.StaffProfileId, nextDay, nextDay, color.Id, null);
    Assert.Equal(45, colorSlots.SlotDurationMinutes);
    AssertStarts(colorSlots, nextDay, (9, 0), (9, 15));

    var corteSlots = await _publicAvailabilityService.GetSlotsAsync(staff.StaffProfileId, nextDay, nextDay, Corte, null);
    AssertStarts(corteSlots, nextDay, (9, 0), (9, 15), (9, 30));

    // A fixed duration (to reschedule an appointment that keeps its own) works the same way.
    var fixedSlots = await _publicAvailabilityService.GetSlotsAsync(staff.StaffProfileId, nextDay, nextDay, null, 70);
    Assert.Equal(70, fixedSlots.SlotDurationMinutes);
    AssertStarts(fixedSlots, nextDay, (9, 0));
  }

  [Fact]
  public async Task GetSlotsAsync_NeverCrossesTheEndOfTheDay()
  {
    var staff = await CreateStaffAsync("slot-midnight@example.com", "Slot Midnight", "Slot Midnight");
    var nextDay = CurrentDate.AddDays(1);
    await _staffAvailabilityService.ReplaceRulesAsync(staff.UserId, [Rule(DayOfWeek.Tuesday, 23, 0, 23, 59)]);

    var response = await _publicAvailabilityService.GetSlotsAsync(staff.StaffProfileId, nextDay, nextDay, Corte, null);

    // 23:30 + 30 minutes would end at midnight, outside the rule (and the day).
    AssertStarts(response, nextDay, (23, 0), (23, 15));
  }

  [Fact]
  public async Task GetSlotsAsync_RequiresAServiceTheProfessionalOffersOrADuration()
  {
    var staff = await CreateStaffAsync("slot-service@example.com", "Slot Service", "Slot Service");
    var nextDay = CurrentDate.AddDays(1);
    var notOffered = await ServiceOfferingTestData.AddGeneralServiceAsync(_dbContext, "Barba");
    var inactiveOffer = await ServiceOfferingTestData.AddGeneralServiceAsync(_dbContext, "Cejas");
    await ServiceOfferingTestData.OfferAsync(_dbContext, staff.StaffProfileId, inactiveOffer.Id, 15, isActive: false);

    var none = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _publicAvailabilityService.GetSlotsAsync(staff.StaffProfileId, nextDay, nextDay, null, null));
    Assert.Contains("generalServiceId", none.Errors.Keys);

    var both = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _publicAvailabilityService.GetSlotsAsync(staff.StaffProfileId, nextDay, nextDay, Corte, 30));
    Assert.Contains("durationMinutes", both.Errors.Keys);

    var tooLong = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _publicAvailabilityService.GetSlotsAsync(staff.StaffProfileId, nextDay, nextDay, null, 241));
    Assert.Contains("durationMinutes", tooLong.Errors.Keys);

    foreach (var serviceId in new[] { notOffered.Id, inactiveOffer.Id })
    {
      var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
          _publicAvailabilityService.GetSlotsAsync(staff.StaffProfileId, nextDay, nextDay, serviceId, null));
      Assert.Contains("generalServiceId", exception.Errors.Keys);
    }

    // An offering of a deactivated general service is not bookable.
    var corte = await _dbContext.GeneralServices.SingleAsync(service => service.Id == Corte);
    corte.Deactivate(CurrentUtc.UtcDateTime);
    await _dbContext.SaveChangesAsync();
    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _publicAvailabilityService.GetSlotsAsync(staff.StaffProfileId, nextDay, nextDay, Corte, null));
  }

  private static void AssertStarts(PublicAvailabilitySlotsResponse response, DateOnly date, params (int Hour, int Minute)[] starts)
      => Assert.Equal(starts.Select(start => ToUtc(date, start.Hour, start.Minute)), response.Slots.Select(slot => slot.StartAtUtc));

  private static Guid Corte => ServiceOfferingTestData.Corte;

  private static AvailabilityRuleRequest Rule(DayOfWeek dayOfWeek, int startHour, int startMinute, int endHour, int endMinute, bool isActive = true)
      => new((int)dayOfWeek, new TimeOnly(startHour, startMinute), new TimeOnly(endHour, endMinute), isActive);

  private async Task<StaffManagementView> CreateStaffAsync(string email, string fullName, string displayName)
  {
    var staff = await _adminStaffService.CreateAsync(new AdminStaffCreateRequest(
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
    await ServiceOfferingTestData.OfferCorteAsync(_dbContext, staff.StaffProfileId);
    return staff;
  }

  private async Task<Appointment> AddAppointmentAsync(
      Guid staffProfileId,
      DateOnly date,
      int startHour,
      int startMinute,
      int endHour,
      int endMinute,
      AppointmentStatus status)
  {
    var appointment = new Appointment(
        staffProfileId,
        "Customer Example",
        ToUtc(date, startHour, startMinute),
        ToUtc(date, endHour, endMinute),
        status,
        AppointmentSource.CustomerBooking,
        CurrentUtc.UtcDateTime,
        null,
        "customer@example.com",
        "+5491100000000",
        null);

    _dbContext.Appointments.Add(appointment);
    await _dbContext.SaveChangesAsync();

    return appointment;
  }

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
}
