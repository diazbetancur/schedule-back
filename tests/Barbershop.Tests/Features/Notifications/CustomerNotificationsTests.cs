using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Loyalty.Redemptions;
using Barbershop.Application.Loyalty.Wallet;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Application.Notifications;
using Barbershop.Domain.Common;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.Services;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Loyalty;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Notifications;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbershop.Tests.Features.Notifications;

/// <summary>
/// ADR-0019 (SONDA BUG-116): membership and points notices queued with the operation that causes them, the scheduled
/// expiry notices, the history and the push preferences per category.
/// </summary>
public sealed class CustomerNotificationsTests : IDisposable
{
  private const string Terms = "La membresía es personal e intransferible.";

  // 2026-11-15 10:00 in Bogotá (inside the 08:00–20:00 window of the expiry notices).
  private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 11, 15, 15, 0, 0, TimeSpan.Zero));
  private readonly AppDbContext _dbContext;
  private readonly MembershipSalesService _memberships;
  private readonly LoyaltyWalletService _wallet;
  private readonly LoyaltyRedemptionService _redemptions;
  private readonly UserNotificationsService _inbox;
  private readonly User _admin;
  private readonly User _customerUser;
  private readonly Service _haircut;

  public CustomerNotificationsTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options;
    _dbContext = new AppDbContext(options);
    _dbContext.Database.EnsureCreated();
    _memberships = new MembershipSalesService(_dbContext, _clock);
    _wallet = new LoyaltyWalletService(_dbContext, _clock);
    _redemptions = new LoyaltyRedemptionService(_dbContext, _clock);
    _inbox = new UserNotificationsService(_dbContext, _clock);

    _admin = new User("Admin Principal", "admin@example.com", "hash", DateTime.UtcNow);
    _customerUser = new User("Carla Cliente", "carla@example.com", "hash", DateTime.UtcNow);
    _haircut = new Service("Corte", 25000, DateTime.UtcNow, 30);
    _dbContext.Users.AddRange(_admin, _customerUser);
    _dbContext.Services.Add(_haircut);
    _dbContext.SaveChanges();

    var settings = _dbContext.LoyaltySettings.Single();
    settings.Update(true, 365, _admin.Id, DateTime.UtcNow);
    _dbContext.SaveChanges();
    _dbContext.ChangeTracker.Clear();
  }

  private DateOnly Today => DateOnly.FromDateTime(BogotaClock.ToLocal(_clock.GetUtcNow().UtcDateTime));

  public void Dispose() => _dbContext.Dispose();

  // ── Memberships ────────────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task Sale_Activation_AndConsumption_NotifyTheLinkedAccount_WithWhatIsLeft_AndAReplayDoesNotRepeat()
  {
    var customerId = await CreateCustomerAsync(linked: true);
    var plan = await CreatePlanAsync(points: null);

    var sold = await _memberships.SellAsync(_admin.Id, customerId, new MembershipSaleRequest(plan.Id, Today));
    var notice = Assert.Single(await NotificationsAsync());
    Assert.Equal((UserNotificationTypes.MembershipSold, "Registramos tu membresía"), (notice.Type, notice.Title));
    Assert.Equal($"/customer/memberships/{sold.Id}", notice.Url);
    Assert.Contains("«Oro»", notice.Body);

    await _memberships.ActivateAsync(_admin.Id, sold.Id, new MembershipActivateRequest());
    var activated = (await NotificationsAsync()).Single(item => item.Type == UserNotificationTypes.MembershipActivated);
    Assert.StartsWith("Tu membresía «Oro» está activa hasta el", activated.Body);

    var ledger = new MembershipLedgerService(_dbContext, _memberships, _clock);
    var request = new MembershipConsumptionRequest(_haircut.Id, 1, Today, null, Guid.NewGuid());
    await ledger.ConsumeAsync(_admin.Id, sold.Id, request);
    await ledger.ConsumeAsync(_admin.Id, sold.Id, request);

    var consumed = Assert.Single(await NotificationsAsync(), item => item.Type == UserNotificationTypes.MembershipConsumed);
    Assert.Equal("Registramos «Corte» con tu membresía «Oro». Te quedan 9 de ese servicio.", consumed.Body);

    // Each notice has its push queued.
    Assert.Equal(3, await _dbContext.NotificationDeliveries.CountAsync(delivery => delivery.UserId == _customerUser.Id));
  }

  [Fact]
  public async Task ASaleActivatedRightAway_SendsASingleNotice_AndAProfileWithoutAccountGetsNothing()
  {
    var plan = await CreatePlanAsync(points: null);
    var linked = await CreateCustomerAsync(linked: true);
    var withoutAccount = await CreateCustomerAsync(linked: false);

    await _memberships.SellAsync(_admin.Id, linked, new MembershipSaleRequest(plan.Id, Today, ActivateNow: true));
    await _memberships.SellAsync(_admin.Id, withoutAccount, new MembershipSaleRequest(plan.Id, Today, ActivateNow: true));

    var notice = Assert.Single(await _dbContext.UserNotifications.AsNoTracking().ToListAsync());
    Assert.Equal((_customerUser.Id, UserNotificationTypes.MembershipActivated), (notice.UserId, notice.Type));
    Assert.StartsWith("Registramos tu membresía «Oro» y ya está activa hasta el", notice.Body);
  }

  // ── Points ─────────────────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task MembershipPoints_FollowThePayment_AndEachChangeIsNotified()
  {
    var customerId = await CreateCustomerAsync(linked: true);
    var plan = await CreatePlanAsync(points: 40);
    var membership = await _memberships.SellAsync(_admin.Id, customerId, new MembershipSaleRequest(plan.Id, Today, ActivateNow: true));
    var income = IncomeEntry.CreateMembershipSale(250000, Today, _admin.Id, DateTime.UtcNow);
    _dbContext.IncomeEntries.Add(income);
    await _dbContext.SaveChangesAsync();

    await _memberships.LinkIncomeAsync(_admin.Id, membership.Id, new MembershipIncomeLinkRequest(income.Id));
    await _memberships.UnlinkIncomeAsync(_admin.Id, membership.Id, new MembershipIncomeUnlinkRequest());
    await _memberships.LinkIncomeAsync(_admin.Id, membership.Id, new MembershipIncomeLinkRequest(income.Id));

    var points = (await NotificationsAsync())
        .Where(item => item.Type.StartsWith("loyalty.", StringComparison.Ordinal))
        .OrderBy(item => item.CreatedAt)
        .Select(item => (item.Type, item.Title))
        .ToList();
    Assert.Equal(
        [
            (UserNotificationTypes.LoyaltyPointsEarned, "Ganaste 40 puntos"),
            (UserNotificationTypes.LoyaltyPointsWithdrawn, "Se retiraron 40 puntos"),
            (UserNotificationTypes.LoyaltyPointsEarned, "Te devolvimos 40 puntos"),
        ],
        points);
  }

  [Fact]
  public async Task ManualAdjustments_AreNotified_WithoutTheInternalReason()
  {
    var customerId = await CreateCustomerAsync(linked: true);

    await _wallet.AdjustAsync(_admin.Id, customerId, new LoyaltyAdjustmentRequest("Credit", 15, "Compensación por demora interna", Guid.NewGuid()));
    await _wallet.AdjustAsync(_admin.Id, customerId, new LoyaltyAdjustmentRequest("Debit", 1, "Error de digitación interno", Guid.NewGuid()));

    var notices = (await NotificationsAsync()).OrderBy(item => item.CreatedAt).ToList();
    Assert.Equal(
        [("Ganaste 15 puntos", "La barbería sumó 15 puntos a tu cuenta."), ("Se retiró 1 punto", "La barbería descontó 1 punto de tu cuenta.")],
        notices.Select(item => (item.Title, item.Body)));
    Assert.DoesNotContain(notices, item => item.Body.Contains("interno", StringComparison.Ordinal));
  }

  [Fact]
  public async Task ApprovedAndRejectedRedemptions_AreNotified_AndARetriedApprovalDoesNotRepeat()
  {
    var customerId = await CreateCustomerAsync(linked: true);
    await _wallet.AdjustAsync(_admin.Id, customerId, new LoyaltyAdjustmentRequest("Credit", 100, "Saldo inicial", Guid.NewGuid()));
    var reward = await _redemptions.CreateAsync(_admin.Id, new LoyaltyRewardUpsertRequest("Gorra", null, "Other", null, 10));

    var first = await _redemptions.RequestAsync(_customerUser.Id, new LoyaltyRedemptionRequest(reward.Id, Guid.NewGuid()));
    var version = (await _redemptions.GetAsync(first.Id)).Version;
    await _redemptions.ApproveAsync(_admin.Id, first.Id, new LoyaltyRedemptionDecisionRequest(version));
    await _redemptions.ApproveAsync(_admin.Id, first.Id, new LoyaltyRedemptionDecisionRequest(version));

    var second = await _redemptions.RequestAsync(_customerUser.Id, new LoyaltyRedemptionRequest(reward.Id, Guid.NewGuid()));
    await _redemptions.RejectAsync(
        _admin.Id, second.Id, new LoyaltyRedemptionReasonRequest((await _redemptions.GetAsync(second.Id)).Version, "Sin existencias"));

    var notices = await NotificationsAsync();
    var approved = Assert.Single(notices, item => item.Type == UserNotificationTypes.LoyaltyRedemptionApproved);
    Assert.StartsWith("Aprobamos tu canje de «Gorra». Reclámalo en la barbería hasta el", approved.Body);
    var rejected = Assert.Single(notices, item => item.Type == UserNotificationTypes.LoyaltyRedemptionRejected);
    Assert.Equal("No aprobamos tu canje de «Gorra». Motivo: Sin existencias. Tus 10 puntos vuelven a estar disponibles.", rejected.Body);
    // SONDA BUG-147 (decision of the user): the points coming back are told on their own too.
    var returned = Assert.Single(notices, item => item.Type == UserNotificationTypes.LoyaltyPointsEarned && item.Title == "Te devolvimos 10 puntos");
    Assert.Equal("Tu canje de «Gorra» no fue aprobado, así que los puntos vuelven a tu cuenta.", returned.Body);
  }

  // SONDA BUG-146: singular and plural agree with the quantity.
  [Fact]
  public async Task ARejectedRedemptionOfOnePoint_SpeaksInSingular()
  {
    var customerId = await CreateCustomerAsync(linked: true);
    await _wallet.AdjustAsync(_admin.Id, customerId, new LoyaltyAdjustmentRequest("Credit", 5, "Saldo inicial", Guid.NewGuid()));
    var reward = await _redemptions.CreateAsync(_admin.Id, new LoyaltyRewardUpsertRequest("Sticker", null, "Other", null, 1));
    var request = await _redemptions.RequestAsync(_customerUser.Id, new LoyaltyRedemptionRequest(reward.Id, Guid.NewGuid()));

    await _redemptions.RejectAsync(
        _admin.Id, request.Id, new LoyaltyRedemptionReasonRequest((await _redemptions.GetAsync(request.Id)).Version, "Sin existencias"));

    var notices = await NotificationsAsync();
    Assert.EndsWith("Tu punto vuelve a estar disponible.", Assert.Single(notices, item => item.Type == UserNotificationTypes.LoyaltyRedemptionRejected).Body);
    Assert.Contains(notices, item => item.Title == "Te devolvimos 1 punto" && item.Body.EndsWith("el punto vuelve a tu cuenta."));
  }

  // ── Scheduled expiry notices ───────────────────────────────────────────────────────────────

  [Fact]
  public async Task ExpiryReminders_WarnSevenDaysAhead_OncePerDate_OnlyInDaytime_AndSkipShortValidities()
  {
    var customerId = await CreateCustomerAsync(linked: true);
    var plan = await CreatePlanAsync(points: null);
    var ending = await _memberships.SellAsync(
        _admin.Id, customerId, new MembershipSaleRequest(plan.Id, Today.AddDays(-30), EndsOn: Today.AddDays(7), ActivateNow: true));
    await _memberships.SellAsync(_admin.Id, customerId, new MembershipSaleRequest(plan.Id, Today, EndsOn: Today.AddDays(8), ActivateNow: true));
    await _memberships.SellAsync(_admin.Id, customerId, new MembershipSaleRequest(plan.Id, Today, EndsOn: Today.AddDays(3), ActivateNow: true));

    // Points: two lots on the same date add up; a reserved part and a short-lived lot are left out.
    var expiresOn = Today.AddDays(5);
    SeedLot(customerId, 30, accruedOn: expiresOn.AddDays(-99), validityDays: 100);
    SeedLot(customerId, 20, accruedOn: expiresOn.AddDays(-99), validityDays: 100, reserved: 5);
    SeedLot(customerId, 50, accruedOn: Today, validityDays: 3);
    await _dbContext.SaveChangesAsync();
    _dbContext.ChangeTracker.Clear();
    await ClearNotificationsAsync();

    var dispatcher = new NotificationDispatcher(_dbContext, _clock, new NotificationWorkerSignal(), NullLogger<NotificationDispatcher>.Instance);
    var reminders = new CustomerExpiryReminderService(_dbContext, dispatcher, _clock);

    Assert.Equal(2, await reminders.RunAsync());
    Assert.Equal(0, await reminders.RunAsync());

    var notices = await NotificationsAsync();
    var membership = Assert.Single(notices, item => item.Type == UserNotificationTypes.MembershipExpiring);
    Assert.Equal($"/customer/memberships/{ending.Id}", membership.Url);
    Assert.StartsWith("Tu membresía «Oro» vence en 7 días", membership.Body);
    var points = Assert.Single(notices, item => item.Type == UserNotificationTypes.LoyaltyPointsExpiring);
    Assert.StartsWith("45 puntos de tu cuenta vencen el", points.Body);

    // At night nothing is created, even what would be new.
    await ClearNotificationsAsync();
    _clock.Now = new DateTimeOffset(2026, 11, 16, 3, 0, 0, TimeSpan.Zero); // 22:00 in Bogotá.
    Assert.Equal(0, await reminders.RunAsync());
  }

  // SONDA BUG-164: a membership with every unit used has nothing left to lose: no «vence pronto».
  [Fact]
  public async Task ExpiryReminders_SkipAMembershipWithoutBalance()
  {
    var customerId = await CreateCustomerAsync(linked: true);
    var plan = await CreatePlanAsync(points: null);
    var used = await _memberships.SellAsync(
        _admin.Id, customerId, new MembershipSaleRequest(plan.Id, Today.AddDays(-30), EndsOn: Today.AddDays(6), ActivateNow: true));
    var ledger = new MembershipLedgerService(_dbContext, _memberships, _clock);
    await ledger.ConsumeAsync(_admin.Id, used.Id, new MembershipConsumptionRequest(_haircut.Id, 10, Today, null, Guid.NewGuid()));
    _dbContext.ChangeTracker.Clear();
    await ClearNotificationsAsync();

    var dispatcher = new NotificationDispatcher(_dbContext, _clock, new NotificationWorkerSignal(), NullLogger<NotificationDispatcher>.Instance);
    Assert.Equal(0, await new CustomerExpiryReminderService(_dbContext, dispatcher, _clock).RunAsync());

    // With one unit given back it has something left again, and it is warned.
    var consumption = await _dbContext.MembershipConsumptions.AsNoTracking().SingleAsync();
    await ledger.ReverseAsync(_admin.Id, used.Id, consumption.Id, new MembershipConsumptionReverseRequest("Corte no realizado"));
    _dbContext.ChangeTracker.Clear();
    Assert.Equal(1, await new CustomerExpiryReminderService(_dbContext, dispatcher, _clock).RunAsync());
  }

  // ── Preferences (push only) and history ────────────────────────────────────────────────────

  [Fact]
  public async Task Preferences_DefaultToOn_ChangeByCategory_AndRejectUnknownOrAccountCategories()
  {
    var defaults = await _inbox.GetPreferencesAsync(_customerUser.Id);
    Assert.Equal(
        [("appointments", true), ("memberships", true), ("loyalty", true), ("purchases", true), ("campaigns", true)],
        defaults.Categories.Select(item => (item.Category, item.PushEnabled)));

    await _inbox.UpdatePreferencesAsync(_customerUser.Id, new NotificationPreferencesUpdateRequest([new("loyalty", false), new("campaigns", false)]));
    var updated = await _inbox.UpdatePreferencesAsync(_customerUser.Id, new NotificationPreferencesUpdateRequest([new("campaigns", true)]));
    Assert.Equal(
        [("appointments", true), ("memberships", true), ("loyalty", false), ("purchases", true), ("campaigns", true)],
        updated.Categories.Select(item => (item.Category, item.PushEnabled)));
    Assert.All((await _inbox.GetPreferencesAsync(_admin.Id)).Categories, item => Assert.True(item.PushEnabled));

    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _inbox.UpdatePreferencesAsync(_customerUser.Id, new NotificationPreferencesUpdateRequest([new("account", false)])));
    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _inbox.UpdatePreferencesAsync(_customerUser.Id, new NotificationPreferencesUpdateRequest([new("security", false)])));
    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _inbox.UpdatePreferencesAsync(_customerUser.Id, new NotificationPreferencesUpdateRequest([new("loyalty", null)])));
    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _inbox.UpdatePreferencesAsync(_customerUser.Id, new NotificationPreferencesUpdateRequest([new("loyalty", true), new("loyalty", false)])));
    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _inbox.UpdatePreferencesAsync(_customerUser.Id, new NotificationPreferencesUpdateRequest([])));
  }

  [Fact]
  public async Task ASwitchedOffCategory_SkipsThePush_ButKeepsTheNoticeInTheBell_AndOtherCategoriesStillGo()
  {
    await _inbox.UpdatePreferencesAsync(_customerUser.Id, new NotificationPreferencesUpdateRequest([new("loyalty", false)]));
    var outbox = new CustomerNotificationOutbox(_dbContext, _clock);
    outbox.Queue(_customerUser.Id, UserNotificationTypes.LoyaltyPointsEarned, new PushNotificationMessage("Ganaste 5 puntos", "Por tu servicio."));
    outbox.Queue(_customerUser.Id, UserNotificationTypes.AppointmentConfirmed, new PushNotificationMessage("Cita confirmada", "Te esperamos."));
    outbox.Queue(_customerUser.Id, "account.security_notice", new PushNotificationMessage("Aviso de cuenta", "Siempre llega."));
    await _dbContext.SaveChangesAsync();

    var channel = new FakePushDeliveryChannel();
    var processor = new NotificationDeliveryProcessor(_dbContext, channel, _clock, NullLogger<NotificationDeliveryProcessor>.Instance);
    await processor.ProcessDueAsync(10);

    Assert.Equal(["Cita confirmada", "Aviso de cuenta"], channel.Calls.Select(call => call.Message.Title).Order().Reverse());
    var deliveries = await _dbContext.NotificationDeliveries.AsNoTracking().ToListAsync();
    Assert.Equal(NotificationDeliveryStatus.Skipped, deliveries.Single(delivery => delivery.Title == "Ganaste 5 puntos").Status);
    Assert.Equal(3, (await _inbox.GetInboxAsync(_customerUser.Id, 10)).UnreadCount);
  }

  [Fact]
  public async Task History_IsNewestFirst_Paginated_FiltersUnread_AndOnlyShowsTheOwnNotifications()
  {
    var outbox = new CustomerNotificationOutbox(_dbContext, _clock);
    for (var index = 1; index <= 5; index++)
    {
      outbox.Queue(_customerUser.Id, UserNotificationTypes.Campaign, new PushNotificationMessage($"Aviso {index}", "Texto."));
      await _dbContext.SaveChangesAsync();
      _clock.Advance(TimeSpan.FromMinutes(1));
    }

    outbox.Queue(_admin.Id, UserNotificationTypes.Campaign, new PushNotificationMessage("De otra persona", "Texto."));
    await _dbContext.SaveChangesAsync();
    var third = await _dbContext.UserNotifications.SingleAsync(item => item.Title == "Aviso 3");
    await _inbox.MarkAsReadAsync(_customerUser.Id, third.Id);

    var first = await _inbox.GetHistoryAsync(_customerUser.Id, 1, 2, unreadOnly: false);
    var second = await _inbox.GetHistoryAsync(_customerUser.Id, 2, 2, unreadOnly: false);
    Assert.Equal((5, 4), (first.TotalCount, first.UnreadCount));
    Assert.Equal(["Aviso 5", "Aviso 4", "Aviso 3", "Aviso 2"], first.Items.Concat(second.Items).Select(item => item.Title));
    Assert.NotNull(second.Items[0].ReadAtUtc);

    var unread = await _inbox.GetHistoryAsync(_customerUser.Id, null, null, unreadOnly: true);
    Assert.Equal((4, 20), (unread.TotalCount, unread.PageSize));
    Assert.DoesNotContain(unread.Items, item => item.Title is "Aviso 3" or "De otra persona");

    await Assert.ThrowsAsync<ValidationProblemException>(() => _inbox.GetHistoryAsync(_customerUser.Id, 0, 20, false));
    await Assert.ThrowsAsync<ValidationProblemException>(() => _inbox.GetHistoryAsync(_customerUser.Id, 1, 51, false));
  }

  [Theory]
  [InlineData("appointment.confirmed", "appointments")]
  [InlineData("staff.hourly_agenda", "appointments")]
  [InlineData("membership.expiring", "memberships")]
  [InlineData("loyalty.points_withdrawn", "loyalty")]
  [InlineData("campaign", "campaigns")]
  [InlineData("account.password_changed", "account")]
  public void EveryType_BelongsToOneCategory_AndUnknownOnesCannotBeSwitchedOff(string type, string category)
  {
    Assert.Equal(category, NotificationCategories.ForType(type));
    Assert.Equal(category != "account", NotificationCategories.IsOptional(category));
  }

  // ── Helpers ────────────────────────────────────────────────────────────────────────────────

  private async Task ClearNotificationsAsync()
  {
    _dbContext.UserNotifications.RemoveRange(await _dbContext.UserNotifications.ToListAsync());
    _dbContext.NotificationDeliveries.RemoveRange(await _dbContext.NotificationDeliveries.ToListAsync());
    await _dbContext.SaveChangesAsync();
    _dbContext.ChangeTracker.Clear();
  }

  private async Task<List<UserNotification>> NotificationsAsync()
      => await _dbContext.UserNotifications.AsNoTracking().Where(item => item.UserId == _customerUser.Id).ToListAsync();

  private async Task<Guid> CreateCustomerAsync(bool linked)
  {
    var profile = new CustomerProfile(linked ? "Carla Cliente" : "Sin Cuenta", linked ? "3001234567" : "3009876543",
        linked ? "carla@example.com" : "sincuenta@example.com", _admin.Id, DateTime.UtcNow);
    if (linked)
    {
      profile.LinkAccount(_customerUser.Id, _admin.Id, DateTime.UtcNow);
    }

    _dbContext.Customers.Add(profile);
    await _dbContext.SaveChangesAsync();
    _dbContext.ChangeTracker.Clear();
    return profile.Id;
  }

  private Task<MembershipPlanView> CreatePlanAsync(int? points)
      => new MembershipPlanManagementService(_dbContext, _clock, new FixedUser(_admin.Id)).CreateAsync(new MembershipPlanUpsertRequest(
          "Oro", null, 250000, "Pool", 10, 90, Terms, null, [new(_haircut.Id, null)], [], points));

  private void SeedLot(Guid customerId, int points, DateOnly accruedOn, int validityDays, int reserved = 0)
  {
    var accruedAt = BogotaClock.ToUtc(accruedOn, new TimeOnly(12, 0));
    var lot = LoyaltyLot.Accrue(customerId, LoyaltySourceType.Adjustment, Guid.NewGuid(), points, accruedAt, accruedOn, validityDays);
    if (reserved > 0)
    {
      lot.Reserve(reserved);
    }

    _dbContext.LoyaltyLots.Add(lot);
  }

  private sealed class FixedUser(Guid userId) : Barbershop.Application.Auth.ICurrentUserAccessor
  {
    public Guid? UserId => userId;
  }
}
