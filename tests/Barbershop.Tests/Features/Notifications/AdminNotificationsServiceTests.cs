using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Notifications;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Configuration;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Notifications;
using Barbershop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Barbershop.Tests.Features.Notifications;

/// <summary>SONDA Lote H: admin broadcasts (BUG-108, BUG-110, BUG-112, BUG-113).</summary>
public sealed class AdminNotificationsServiceTests : IDisposable
{
  private static readonly DateTimeOffset Now = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

  private readonly AppDbContext _dbContext;
  private readonly RecordingNotificationDispatcher _dispatcher = new();
  private readonly ManualTimeProvider _time = new(Now);
  private readonly AdminNotificationsService _service;

  public AdminNotificationsServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    _service = new AdminNotificationsService(_dbContext, _dispatcher, _time);

    new IdentitySeedService(
        _dbContext,
        new PasswordHasher<object>(),
        Options.Create(new SeedAdminOptions()),
        new TestHostEnvironment(),
        TimeProvider.System).EnsureSeededAsync().GetAwaiter().GetResult();
  }

  public void Dispose() => _dbContext.Dispose();

  [Theory]
  [InlineData("3001234567")]
  [InlineData("+57 300 123 4567")]
  [InlineData("57-300-123-4567")]
  [InlineData("300 123")]
  [InlineData("4567")]
  public async Task SearchCustomersAsync_FindsByPhone_IgnoringCountryCodeSpacesAndDashes(string search)
  {
    var customer = await CreateUserAsync("ana@example.com", "Ana Pérez", RoleNames.Customer, phone: "+57 300-123-4567");
    await CreateUserAsync("beto@example.com", "Beto Ruiz", RoleNames.Customer, phone: "3109998877");

    var results = await _service.SearchCustomersAsync(search);

    Assert.Equal(customer.Id, Assert.Single(results).Id);
  }

  [Fact]
  public async Task SearchCustomersAsync_StillFindsByNameAndEmail_AndSkipsInactiveCustomers()
  {
    var ana = await CreateUserAsync("ana@example.com", "Ana Pérez", RoleNames.Customer, phone: "3001234567");
    var inactive = await CreateUserAsync("ana.old@example.com", "Ana Vieja", RoleNames.Customer, phone: "3001234567");
    inactive.Deactivate(Now.UtcDateTime);
    await _dbContext.SaveChangesAsync();

    Assert.Equal(ana.Id, Assert.Single(await _service.SearchCustomersAsync("pérez")).Id);
    Assert.Equal(ana.Id, Assert.Single(await _service.SearchCustomersAsync("ana@")).Id);
    Assert.Equal(ana.Id, Assert.Single(await _service.SearchCustomersAsync("300 123 4567")).Id);
  }

  [Theory]
  [InlineData("+57 300 123 4567", "3001234567")]
  [InlineData("0057 3001234567", "3001234567")]
  [InlineData("300-123-4567", "3001234567")]
  [InlineData("571234", "571234")]
  public void NationalDigits_KeepsOnlyTheNationalNumber(string phone, string expected)
      => Assert.Equal(expected, AdminNotificationsService.NationalDigits(phone));

  [Fact]
  public async Task FilterByStaff_ReachesOnlyActiveCustomersWithANonCancelledAppointment()
  {
    var staff = await CreateStaffAsync();
    var active = await CreateUserAsync("active@example.com", "Activo", RoleNames.Customer);
    var disabled = await CreateUserAsync("disabled@example.com", "Desactivado", RoleNames.Customer);
    var onlyCancelled = await CreateUserAsync("cancelled@example.com", "Solo cancelada", RoleNames.Customer);
    var otherStaffUser = await CreateUserAsync("staff2@example.com", "Otro barbero", RoleNames.Staff);
    disabled.Deactivate(Now.UtcDateTime);
    AddAppointment(staff.Id, active.Id, AppointmentStatus.Completed);
    AddAppointment(staff.Id, active.Id, AppointmentStatus.Confirmed);
    AddAppointment(staff.Id, disabled.Id, AppointmentStatus.Completed);
    AddAppointment(staff.Id, onlyCancelled.Id, AppointmentStatus.Cancelled);
    AddAppointment(staff.Id, otherStaffUser.Id, AppointmentStatus.Completed);
    await _dbContext.SaveChangesAsync();

    var preview = await _service.PreviewAsync(Request(NotificationTargetTypes.Filter, staffProfileId: staff.Id));
    var admin = await CreateUserAsync("admin2@example.com", "Admin", RoleNames.Admin);
    await _service.BroadcastAsync(admin.Id, Request(NotificationTargetTypes.Filter, staffProfileId: staff.Id));

    Assert.Equal((1, "Clientes de Barbero Uno"), (preview.RecipientCount, preview.TargetSummary));
    Assert.Equal(new[] { active.Id }, Assert.Single(_dispatcher.Calls).UserIds);
  }

  [Fact]
  public async Task PreviewAsync_CountsTheRecipientsWithoutSendingOrStoringAnything()
  {
    await CreateUserAsync("c1@example.com", "Cliente Uno", RoleNames.Customer);
    await CreateUserAsync("c2@example.com", "Cliente Dos", RoleNames.Customer);
    var inactive = await CreateUserAsync("c3@example.com", "Cliente Tres", RoleNames.Customer);
    inactive.Deactivate(Now.UtcDateTime);
    await _dbContext.SaveChangesAsync();

    var preview = await _service.PreviewAsync(Request(NotificationTargetTypes.All));

    Assert.Equal((2, "Todos los clientes"), (preview.RecipientCount, preview.TargetSummary));
    Assert.Empty(_dispatcher.Calls);
    Assert.Empty(_dbContext.NotificationCampaigns);
  }

  [Fact]
  public async Task PreviewAsync_ValidatesLikeTheBroadcast()
  {
    var error = await Assert.ThrowsAsync<ValidationProblemException>(
        () => _service.PreviewAsync(new NotificationBroadcastRequest("Hola", "Texto", "selected", [], null)));

    Assert.Contains("customerUserIds", error.Errors.Keys);
  }

  [Fact]
  public async Task BroadcastAsync_RejectsTheSameTextToTheSameAudienceWithinTheWindow_InSpanish()
  {
    var admin = await CreateUserAsync("admin2@example.com", "Admin", RoleNames.Admin);
    await CreateUserAsync("c1@example.com", "Cliente Uno", RoleNames.Customer);

    await _service.BroadcastAsync(admin.Id, Request(NotificationTargetTypes.All));
    _time.Advance(TimeSpan.FromMinutes(2));
    var error = await Assert.ThrowsAsync<ConflictException>(
        () => _service.BroadcastAsync(admin.Id, Request(NotificationTargetTypes.All)));

    Assert.StartsWith("Ya se envió esta misma notificación", error.Message);
    Assert.Single(_dispatcher.Calls);
    Assert.Single(_dbContext.NotificationCampaigns);
  }

  [Fact]
  public async Task BroadcastAsync_AllowsADifferentTextOrTheSameOneAfterTheWindow()
  {
    var admin = await CreateUserAsync("admin2@example.com", "Admin", RoleNames.Admin);
    await CreateUserAsync("c1@example.com", "Cliente Uno", RoleNames.Customer);

    await _service.BroadcastAsync(admin.Id, Request(NotificationTargetTypes.All));
    await _service.BroadcastAsync(admin.Id, Request(NotificationTargetTypes.All) with { Body = "Otro texto" });
    _time.Advance(AdminNotificationsService.DuplicateWindow + TimeSpan.FromSeconds(1));
    await _service.BroadcastAsync(admin.Id, Request(NotificationTargetTypes.All));

    Assert.Equal(3, _dispatcher.Calls.Count);
  }

  private static NotificationBroadcastRequest Request(string targetType, Guid? staffProfileId = null)
      => new("Promo de noviembre", "Corte con 10% de descuento esta semana.", targetType, null, staffProfileId);

  private void AddAppointment(Guid staffProfileId, Guid customerUserId, AppointmentStatus status)
  {
    var startsAt = Now.UtcDateTime.AddDays(-_dbContext.ChangeTracker.Entries<Appointment>().Count() - 1);
    _dbContext.Appointments.Add(new Appointment(
        staffProfileId, "Cliente", startsAt, startsAt.AddMinutes(30), status, AppointmentSource.Manual, Now.UtcDateTime, customerUserId));
  }

  private async Task<StaffProfile> CreateStaffAsync()
  {
    var user = await CreateUserAsync("staff1@example.com", "Barbero Uno", RoleNames.Staff);
    var profile = new StaffProfile(user.Id, "Barbero Uno", 30, Now.UtcDateTime);
    _dbContext.StaffProfiles.Add(profile);
    await _dbContext.SaveChangesAsync();
    return profile;
  }

  private async Task<User> CreateUserAsync(string email, string fullName, string roleName, string? phone = null)
  {
    var user = new User(fullName, email, "hash", Now.UtcDateTime, phone);
    var role = await _dbContext.Roles.SingleAsync(r => r.NormalizedName == roleName.ToUpperInvariant());
    user.UserRoles.Add(new UserRole(user.Id, role.Id, Now.UtcDateTime));
    _dbContext.Users.Add(user);
    await _dbContext.SaveChangesAsync();
    return user;
  }

  private sealed class TestHostEnvironment : IHostEnvironment
  {
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "Barbershop.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(AppContext.BaseDirectory);
  }
}
