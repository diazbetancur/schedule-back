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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Barbershop.Tests.Features.Notifications;

public sealed class AppointmentNotificationServiceTests : IDisposable
{
  private readonly AppDbContext _dbContext;
  private readonly FakePushNotificationSender _sender;
  private readonly IAppointmentNotificationService _service;

  public AppointmentNotificationServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    _sender = new FakePushNotificationSender();
    var dispatcher = new NotificationDispatcher(_dbContext, _sender, TimeProvider.System, NullLogger<NotificationDispatcher>.Instance);
    _service = new AppointmentNotificationService(dispatcher, _dbContext);

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
  public async Task NotifyStaffOfNewAppointmentAsync_SendsToStaffAndAllAdmins()
  {
    var staff = await CreateUserAsync("staff@example.com", "Staff Person", RoleNames.Staff);
    var admin = await CreateUserAsync("admin@example.com", "Admin Person", RoleNames.Admin);

    await _service.NotifyStaffOfNewAppointmentAsync(Context(staff.Id));

    var call = Assert.Single(_sender.Calls);
    Assert.Contains(staff.Id, call.UserIds);
    Assert.Contains(admin.Id, call.UserIds);
    Assert.Equal(2, call.UserIds.Count);
  }

  [Fact]
  public async Task NotifyStaffOfCustomerCancellationAsync_SendsToStaffAndAllAdmins()
  {
    var staff = await CreateUserAsync("staff2@example.com", "Staff Two", RoleNames.Staff);
    var admin = await CreateUserAsync("admin2@example.com", "Admin Two", RoleNames.Admin);

    await _service.NotifyStaffOfCustomerCancellationAsync(Context(staff.Id));

    var call = Assert.Single(_sender.Calls);
    Assert.Contains(staff.Id, call.UserIds);
    Assert.Contains(admin.Id, call.UserIds);
    Assert.Equal(2, call.UserIds.Count);
  }

  [Fact]
  public async Task NotifyStaffOfNewAppointmentAsync_WhenStaffIsAlsoAdmin_DoesNotDuplicateRecipient()
  {
    var staffAdmin = await CreateUserAsync("owner@example.com", "Owner", RoleNames.Staff, RoleNames.Admin);

    await _service.NotifyStaffOfNewAppointmentAsync(Context(staffAdmin.Id));

    var call = Assert.Single(_sender.Calls);
    Assert.Single(call.UserIds);
    Assert.Equal(staffAdmin.Id, call.UserIds.Single());
  }

  [Fact]
  public async Task NotifyStaffOfNewAppointmentAsync_IgnoresInactiveAdmins()
  {
    var staff = await CreateUserAsync("staff3@example.com", "Staff Three", RoleNames.Staff);
    var inactiveAdmin = await CreateUserAsync("admin3@example.com", "Admin Three", RoleNames.Admin);
    inactiveAdmin.Deactivate(DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();

    await _service.NotifyStaffOfNewAppointmentAsync(Context(staff.Id));

    var call = Assert.Single(_sender.Calls);
    Assert.Single(call.UserIds);
    Assert.Equal(staff.Id, call.UserIds.Single());
  }

  [Fact]
  public async Task NotifyCustomerOfAppointmentCancellationAsync_DoesNotIncludeAdmins()
  {
    await CreateUserAsync("admin4@example.com", "Admin Four", RoleNames.Admin);
    var customer = await CreateUserAsync("customer@example.com", "Customer One", RoleNames.Customer);

    await _service.NotifyCustomerOfAppointmentCancellationAsync(
        new AppointmentNotificationContext(Guid.NewGuid(), "Staff Display", customer.Id, "Customer One", DateTime.UtcNow.AddDays(1)));

    var call = Assert.Single(_sender.Calls);
    Assert.Equal(new[] { customer.Id }, call.UserIds);
  }

  [Fact]
  public async Task NotifyCustomerOfAppointmentConfirmationAsync_TagsMessageWithAppointmentId()
  {
    var customer = await CreateUserAsync("customer5@example.com", "Customer Five", RoleNames.Customer);
    var appointmentId = Guid.NewGuid();

    await _service.NotifyCustomerOfAppointmentConfirmationAsync(
        new AppointmentNotificationContext(Guid.NewGuid(), "Staff Display", customer.Id, "Customer Five", DateTime.UtcNow.AddDays(1), appointmentId));

    var call = Assert.Single(_sender.Calls);
    Assert.Equal($"appointment-{appointmentId:N}", call.Message.Tag);
    Assert.Equal("/customer/appointments", call.Message.Url);
  }

  [Fact]
  public async Task NotifyStaffOfNewAppointmentAsync_SavesOneInboxEntryPerRecipient()
  {
    var staff = await CreateUserAsync("staff6@example.com", "Staff Six", RoleNames.Staff);
    var admin = await CreateUserAsync("admin6@example.com", "Admin Six", RoleNames.Admin);

    await _service.NotifyStaffOfNewAppointmentAsync(Context(staff.Id));

    var inbox = await _dbContext.UserNotifications.ToListAsync();
    Assert.Equal(2, inbox.Count);
    Assert.Contains(inbox, notification => notification.UserId == staff.Id);
    Assert.Contains(inbox, notification => notification.UserId == admin.Id);
    Assert.All(inbox, notification =>
    {
      Assert.Equal(UserNotificationTypes.AppointmentCreated, notification.Type);
      Assert.Equal("Nueva cita agendada", notification.Title);
      Assert.Equal("/staff/appointments", notification.Url);
      Assert.Null(notification.ReadAt);
    });
  }

  private static AppointmentNotificationContext Context(Guid staffUserId)
      => new(staffUserId, "Staff Display", null, "Customer Name", DateTime.UtcNow.AddDays(1));

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

  private sealed class TestHostEnvironment : IHostEnvironment
  {
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "Barbershop.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(AppContext.BaseDirectory);
  }

  private sealed class FakePushNotificationSender : IPushNotificationSender
  {
    public List<(IReadOnlyCollection<Guid> UserIds, PushNotificationMessage Message)> Calls { get; } = [];

    public Task SendToUsersAsync(IReadOnlyCollection<Guid> userIds, PushNotificationMessage message, CancellationToken cancellationToken = default)
    {
      Calls.Add((userIds, message));
      return Task.CompletedTask;
    }
  }
}
