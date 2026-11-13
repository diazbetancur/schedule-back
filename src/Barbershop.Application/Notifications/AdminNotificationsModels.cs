namespace Barbershop.Application.Notifications;

public static class NotificationTargetTypes
{
  public const string All = "all";
  public const string Selected = "selected";
  public const string Filter = "filter";
}

public sealed record NotificationBroadcastRequest(
    string Title,
    string Body,
    string TargetType,
    IReadOnlyCollection<Guid>? CustomerUserIds,
    Guid? StaffProfileId);

public sealed record NotificationCampaignView(
    Guid Id,
    string Title,
    string Body,
    string TargetSummary,
    int RecipientCount,
    string SentByName,
    DateTime CreatedAtUtc);

public sealed record CustomerSummaryView(
    Guid Id,
    string FullName,
    string Email,
    string? PhoneNumber);

/// <summary>
/// BUG-110: who a broadcast would reach, read before sending it (nothing is sent nor stored), so the admin confirms
/// with the number of recipients.
/// </summary>
public sealed record NotificationBroadcastPreviewView(int RecipientCount, string TargetSummary);
