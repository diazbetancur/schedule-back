using Barbershop.Application.Auth;
using Barbershop.Application.Notifications;
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

public sealed class AppointmentNotificationServiceTests : IDisposable
{
  private const string StaffUrl = "/staff/appointments";
  private const string AdminUrl = "/admin/appointments";

  private readonly AppDbContext _dbContext;
  private readonly RecordingNotificationDispatcher _dispatcher;
  private readonly TestCurrentUserAccessor _currentUser = new();
  private readonly IAppointmentNotificationService _service;

  public AppointmentNotificationServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    _dispatcher = new RecordingNotificationDispatcher();
    _service = new AppointmentNotificationService(_dispatcher, _dbContext, _currentUser);

    var seedService = new IdentitySeedService(
        _dbContext,
        new PasswordHasher<object>(),
        Options.Create(new SeedAdminOptions()),
        new TestHostEnvironment(),
        TimeProvider.System);
    seedService.EnsureSeededAsync().GetAwaiter().GetResult();
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task NotifyStaffOfNewAppointmentAsync_SendsToStaffAndAdmins_WithTheirOwnLinks()
  {
    var staff = await CreateUserAsync("staff@example.com", "Staff Person", RoleNames.Staff);
    var admin = await CreateUserAsync("admin@example.com", "Admin Person", RoleNames.Admin);

    await _service.NotifyStaffOfNewAppointmentAsync(Context(staff.Id, customerUserId: Guid.NewGuid()));

    Assert.Equal(2, _dispatcher.Calls.Count);

    var staffCall = CallFor(staff.Id);
    Assert.Equal(new[] { staff.Id }, staffCall.UserIds);
    Assert.Equal(StaffUrl, staffCall.Message.Url);
    Assert.StartsWith("Customer Name agendó una cita para el ", staffCall.Message.Body);
    Assert.Equal(UserNotificationTypes.AppointmentCreated, staffCall.Type);

    var adminCall = CallFor(admin.Id);
    Assert.Equal(new[] { admin.Id }, adminCall.UserIds);
    Assert.Equal(AdminUrl, adminCall.Message.Url);
    Assert.StartsWith("Customer Name agendó una cita con Staff Display para el ", adminCall.Message.Body);
    Assert.Equal(UserNotificationTypes.AppointmentCreated, adminCall.Type);
  }

  [Fact]
  public async Task NotifyStaffOfCustomerCancellationAsync_SendsToStaffAndAdmins()
  {
    var staff = await CreateUserAsync("staff2@example.com", "Staff Two", RoleNames.Staff);
    var admin = await CreateUserAsync("admin2@example.com", "Admin Two", RoleNames.Admin);

    await _service.NotifyStaffOfCustomerCancellationAsync(Context(staff.Id, customerUserId: Guid.NewGuid()));

    Assert.Equal(2, _dispatcher.Calls.Count);
    Assert.All(_dispatcher.Calls, call => Assert.Equal(UserNotificationTypes.AppointmentCancelledByCustomer, call.Type));
    Assert.Contains("con Staff Display", CallFor(admin.Id).Message.Body);
    Assert.DoesNotContain("Staff Display", CallFor(staff.Id).Message.Body);
  }

  [Fact]
  public async Task NotifyStaffOfNewAppointmentAsync_WhenStaffIsAlsoAdmin_OnlyGetsTheStaffNotification()
  {
    var staffAdmin = await CreateUserAsync("owner@example.com", "Owner", RoleNames.Staff, RoleNames.Admin);

    await _service.NotifyStaffOfNewAppointmentAsync(Context(staffAdmin.Id));

    var call = Assert.Single(_dispatcher.Calls);
    Assert.Equal(new[] { staffAdmin.Id }, call.UserIds);
    Assert.Equal(StaffUrl, call.Message.Url);
  }

  [Fact]
  public async Task NotifyStaffOfNewAppointmentAsync_IgnoresInactiveAdmins()
  {
    var staff = await CreateUserAsync("staff3@example.com", "Staff Three", RoleNames.Staff);
    var inactiveAdmin = await CreateUserAsync("admin3@example.com", "Admin Three", RoleNames.Admin);
    inactiveAdmin.Deactivate(DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();

    await _service.NotifyStaffOfNewAppointmentAsync(Context(staff.Id));

    var call = Assert.Single(_dispatcher.Calls);
    Assert.Equal(new[] { staff.Id }, call.UserIds);
  }

  [Fact]
  public async Task NotifyStaffOfNewAppointmentAsync_WhenAdminCreatesIt_SkipsThatAdmin()
  {
    var staff = await CreateUserAsync("staff4@example.com", "Staff Four", RoleNames.Staff);
    var actingAdmin = await CreateUserAsync("admin4@example.com", "Admin Four", RoleNames.Admin);
    var otherAdmin = await CreateUserAsync("admin5@example.com", "Admin Five", RoleNames.Admin);
    _currentUser.UserId = actingAdmin.Id;

    await _service.NotifyStaffOfNewAppointmentAsync(Context(staff.Id));

    Assert.Equal(2, _dispatcher.Calls.Count);
    Assert.Equal(new[] { staff.Id }, CallFor(staff.Id).UserIds);
    Assert.Equal(new[] { otherAdmin.Id }, CallFor(otherAdmin.Id).UserIds);
    Assert.DoesNotContain(_dispatcher.Calls, call => call.UserIds.Contains(actingAdmin.Id));
  }

  [Fact]
  public async Task NotifyStaffOfNewAppointmentAsync_WhenStaffCreatesManualAppointment_OnlyAdminsAreNotified()
  {
    var staff = await CreateUserAsync("staff5@example.com", "Staff Five", RoleNames.Staff);
    var admin = await CreateUserAsync("admin6@example.com", "Admin Six", RoleNames.Admin);
    _currentUser.UserId = staff.Id;

    await _service.NotifyStaffOfNewAppointmentAsync(Context(staff.Id));

    var call = Assert.Single(_dispatcher.Calls);
    Assert.Equal(new[] { admin.Id }, call.UserIds);
    Assert.StartsWith("Se agendó una cita de Customer Name con Staff Display para el ", call.Message.Body);
  }

  [Fact]
  public async Task NotifyStaffOfAppointmentRescheduledAsync_WhenAdminMovesIt_NotifiesStaffAndOtherAdmins()
  {
    var staff = await CreateUserAsync("staff6@example.com", "Staff Six", RoleNames.Staff);
    var actingAdmin = await CreateUserAsync("admin7@example.com", "Admin Seven", RoleNames.Admin);
    var otherAdmin = await CreateUserAsync("admin8@example.com", "Admin Eight", RoleNames.Admin);
    _currentUser.UserId = actingAdmin.Id;

    await _service.NotifyStaffOfAppointmentRescheduledAsync(Context(staff.Id));

    Assert.Equal(2, _dispatcher.Calls.Count);
    Assert.All(_dispatcher.Calls, call =>
    {
      Assert.Equal(UserNotificationTypes.AppointmentUpdated, call.Type);
      Assert.Equal("Cita reprogramada", call.Message.Title);
    });
    Assert.StartsWith("La cita de Customer Name ahora es el ", CallFor(staff.Id).Message.Body);
    Assert.StartsWith("La cita de Customer Name con Staff Display ahora es el ", CallFor(otherAdmin.Id).Message.Body);
  }

  [Fact]
  public async Task NotifyStaffOfAppointmentCancelledAsync_WhenStaffCancels_OnlyAdminsAreNotified()
  {
    var staff = await CreateUserAsync("staff7@example.com", "Staff Seven", RoleNames.Staff);
    var admin = await CreateUserAsync("admin9@example.com", "Admin Nine", RoleNames.Admin);
    _currentUser.UserId = staff.Id;

    await _service.NotifyStaffOfAppointmentCancelledAsync(Context(staff.Id));

    var call = Assert.Single(_dispatcher.Calls);
    Assert.Equal(new[] { admin.Id }, call.UserIds);
    Assert.Equal(UserNotificationTypes.AppointmentCancelled, call.Type);
    Assert.Equal(AdminUrl, call.Message.Url);
    Assert.StartsWith("Se canceló la cita de Customer Name con Staff Display del ", call.Message.Body);
  }

  [Fact]
  public async Task NotifyCustomerOfAppointmentCancellationAsync_DoesNotIncludeAdmins()
  {
    await CreateUserAsync("admin10@example.com", "Admin Ten", RoleNames.Admin);
    var customer = await CreateUserAsync("customer@example.com", "Customer One", RoleNames.Customer);

    await _service.NotifyCustomerOfAppointmentCancellationAsync(
        new AppointmentNotificationContext(Guid.NewGuid(), "Staff Display", customer.Id, "Customer One", DateTime.UtcNow.AddDays(1)));

    var call = Assert.Single(_dispatcher.Calls);
    Assert.Equal(new[] { customer.Id }, call.UserIds);
    Assert.Equal(UserNotificationTypes.AppointmentCancelled, call.Type);
  }

  [Fact]
  public async Task NotifyCustomer_WhenAdminActs_SaysTheShopDidIt()
  {
    var admin = await CreateUserAsync("admin11@example.com", "Admin Eleven", RoleNames.Admin);
    var customer = await CreateUserAsync("customer2@example.com", "Customer Two", RoleNames.Customer);
    _currentUser.UserId = admin.Id;

    await _service.NotifyCustomerOfAppointmentCancellationAsync(
        new AppointmentNotificationContext(Guid.NewGuid(), "Staff Display", customer.Id, "Customer Two", DateTime.UtcNow.AddDays(1)));

    var call = Assert.Single(_dispatcher.Calls);
    Assert.StartsWith("La barbería canceló tu cita del ", call.Message.Body);
  }

  [Fact]
  public async Task NotifyCustomer_WhenStaffActs_UsesTheStaffName()
  {
    var staff = await CreateUserAsync("staff8@example.com", "Staff Eight", RoleNames.Staff);
    var customer = await CreateUserAsync("customer3@example.com", "Customer Three", RoleNames.Customer);
    _currentUser.UserId = staff.Id;

    await _service.NotifyCustomerOfAppointmentUpdateAsync(
        new AppointmentNotificationContext(staff.Id, "Staff Display", customer.Id, "Customer Three", DateTime.UtcNow.AddDays(1)));

    var call = Assert.Single(_dispatcher.Calls);
    Assert.StartsWith("Staff Display modificó tu cita.", call.Message.Body);
  }

  [Fact]
  public async Task NotifyCustomerOfAppointmentConfirmationAsync_TagsMessageWithAppointmentId()
  {
    var customer = await CreateUserAsync("customer5@example.com", "Customer Five", RoleNames.Customer);
    var appointmentId = Guid.NewGuid();

    await _service.NotifyCustomerOfAppointmentConfirmationAsync(
        new AppointmentNotificationContext(Guid.NewGuid(), "Staff Display", customer.Id, "Customer Five", DateTime.UtcNow.AddDays(1), appointmentId));

    var call = Assert.Single(_dispatcher.Calls);
    Assert.Equal($"appointment-{appointmentId:N}", call.Message.Tag);
    Assert.Equal("/customer/appointments", call.Message.Url);
    Assert.Equal(UserNotificationTypes.AppointmentConfirmed, call.Type);
  }

  [Fact]
  public async Task NotifyCustomer_WithoutCustomerAccount_DoesNothing()
  {
    await _service.NotifyCustomerOfAppointmentUpdateAsync(
        new AppointmentNotificationContext(Guid.NewGuid(), "Staff Display", null, "Walk-in", DateTime.UtcNow.AddDays(1)));

    Assert.Empty(_dispatcher.Calls);
  }

  private (IReadOnlyCollection<Guid> UserIds, string Type, PushNotificationMessage Message, NotificationDispatchOptions? Options) CallFor(Guid userId)
      => Assert.Single(_dispatcher.Calls, call => call.UserIds.Contains(userId));

  private static AppointmentNotificationContext Context(Guid staffUserId, Guid? customerUserId = null)
      => new(staffUserId, "Staff Display", customerUserId, "Customer Name", DateTime.UtcNow.AddDays(1));

  private async Task<User> CreateUserAsync(string email, string fullName, params string[] roleNames)
  {
    var utcNow = DateTime.UtcNow;
    var user = new User(fullName, email, "hash", utcNow, null);

    foreach (var roleName in roleNames)
    {
      var role = await _dbContext.Roles.SingleAsync(r => r.NormalizedName == roleName.ToUpperInvariant());
      user.UserRoles.Add(new UserRole(user.Id, role.Id, utcNow));
    }

    _dbContext.Users.Add(user);
    await _dbContext.SaveChangesAsync();
    return user;
  }

  private sealed class TestCurrentUserAccessor : ICurrentUserAccessor
  {
    public Guid? UserId { get; set; }
  }

  private sealed class TestHostEnvironment : IHostEnvironment
  {
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "Barbershop.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(AppContext.BaseDirectory);
  }
}
