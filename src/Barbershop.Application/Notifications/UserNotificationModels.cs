namespace Barbershop.Application.Notifications;

public static class UserNotificationTypes
{
  public const string AppointmentCreated = "appointment.created";
  public const string AppointmentCancelledByCustomer = "appointment.cancelled_by_customer";
  public const string AppointmentUpdated = "appointment.updated";
  public const string AppointmentCancelled = "appointment.cancelled";
  public const string AppointmentConfirmed = "appointment.confirmed";
  public const string Campaign = "campaign";
  public const string AppointmentReminder = "appointment.reminder";
  public const string StaffHourlyAgenda = "staff.hourly_agenda";

  // ADR-0019 N1: memberships.
  public const string MembershipSold = "membership.sold";
  public const string MembershipActivated = "membership.activated";
  public const string MembershipConsumed = "membership.consumed";
  public const string MembershipExpiring = "membership.expiring";

  // ADR-0019 N1: loyalty points.
  public const string LoyaltyPointsEarned = "loyalty.points_earned";
  public const string LoyaltyPointsWithdrawn = "loyalty.points_withdrawn";
  public const string LoyaltyRedemptionApproved = "loyalty.redemption_approved";
  public const string LoyaltyRedemptionRejected = "loyalty.redemption_rejected";
  public const string LoyaltyPointsExpiring = "loyalty.points_expiring";
}

/// <summary>
/// ADR-0019 N1/N4: what a user can switch off (push only). Every type belongs to one category; a type outside the four
/// optional ones is an account or security notice and is always delivered.
/// </summary>
public static class NotificationCategories
{
  public const string Appointments = "appointments";
  public const string Memberships = "memberships";
  public const string Loyalty = "loyalty";
  public const string Campaigns = "campaigns";
  public const string Account = "account";

  /// <summary>The categories the user may switch off, in the order the settings screen shows them.</summary>
  public static IReadOnlyList<string> Optional { get; } = [Appointments, Memberships, Loyalty, Campaigns];

  public static string ForType(string type)
  {
    if (type.StartsWith("appointment.", StringComparison.Ordinal) || type == UserNotificationTypes.StaffHourlyAgenda)
    {
      return Appointments;
    }

    if (type.StartsWith("membership.", StringComparison.Ordinal))
    {
      return Memberships;
    }

    if (type.StartsWith("loyalty.", StringComparison.Ordinal))
    {
      return Loyalty;
    }

    return type == UserNotificationTypes.Campaign ? Campaigns : Account;
  }

  public static bool IsOptional(string category) => Optional.Contains(category, StringComparer.Ordinal);
}

public sealed record UserNotificationView(
    Guid Id,
    string Type,
    string Title,
    string Body,
    string? Url,
    DateTime CreatedAtUtc,
    DateTime? ReadAtUtc);

/// <param name="HasMore">BUG-107: more notifications after this page (the panel offers «Ver más»).</param>
public sealed record UserNotificationInboxView(int UnreadCount, IReadOnlyList<UserNotificationView> Items, bool HasMore = false);

/// <summary>ADR-0019 N5: one page of the notification history (newest first) and the unread total.</summary>
public sealed record UserNotificationHistoryView(
    IReadOnlyList<UserNotificationView> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int UnreadCount);

/// <summary>ADR-0019 N4: whether the push of a category is on (default: on).</summary>
public sealed record NotificationPreferenceView(string Category, bool PushEnabled);

public sealed record NotificationPreferencesView(IReadOnlyList<NotificationPreferenceView> Categories);

public sealed record NotificationPreferenceUpdate(string? Category, bool? PushEnabled);

public sealed record NotificationPreferencesUpdateRequest(IReadOnlyList<NotificationPreferenceUpdate>? Categories);
