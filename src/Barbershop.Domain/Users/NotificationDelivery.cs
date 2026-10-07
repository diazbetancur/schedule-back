using Barbershop.Domain.Common;

namespace Barbershop.Domain.Users;

public static class NotificationDeliveryStatus
{
  public const string Pending = "pending";
  public const string Sent = "sent";
  public const string Failed = "failed";
  public const string Skipped = "skipped";
}

public static class NotificationDeliveryChannel
{
  public const string Push = "push";
}

/// <summary>
/// One queued delivery of a notification to one user through one channel (today: push).
/// Processed by a background worker with retries, so the request that triggered it never waits on it.
/// </summary>
public sealed class NotificationDelivery
{
  public const int MaxAttempts = 5;
  private const char IdSeparator = ',';

  private static readonly TimeSpan[] RetryDelays =
  [
    TimeSpan.FromSeconds(30),
    TimeSpan.FromMinutes(2),
    TimeSpan.FromMinutes(10),
    TimeSpan.FromMinutes(30),
  ];

  private NotificationDelivery()
  {
  }

  public NotificationDelivery(
      Guid userId,
      Guid? userNotificationId,
      string channel,
      string title,
      string body,
      string? url,
      string? tag,
      bool isTimeSensitive,
      DateTime createdAt,
      DateTime expiresAt)
  {
    UserId = userId;
    UserNotificationId = userNotificationId;
    Channel = DomainValidation.Required(channel, nameof(channel), 16);
    Title = DomainValidation.Required(title, nameof(title), 160);
    Body = DomainValidation.Required(body, nameof(body), 1000);
    Url = DomainValidation.Optional(url, 512);
    Tag = DomainValidation.Optional(tag, 120);
    IsTimeSensitive = isTimeSensitive;
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
    ExpiresAt = DomainValidation.EnsureUtc(expiresAt, nameof(expiresAt));
    NextAttemptAt = CreatedAt;
    Status = NotificationDeliveryStatus.Pending;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid UserId { get; private set; }
  public Guid? UserNotificationId { get; private set; }
  public string Channel { get; private set; } = string.Empty;
  public string Title { get; private set; } = string.Empty;
  public string Body { get; private set; } = string.Empty;
  public string? Url { get; private set; }
  public string? Tag { get; private set; }
  public bool IsTimeSensitive { get; private set; }
  public string Status { get; private set; } = string.Empty;
  public int Attempts { get; private set; }
  public DateTime NextAttemptAt { get; private set; }
  public DateTime ExpiresAt { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? CompletedAt { get; private set; }
  public string? LastError { get; private set; }

  /// <summary>Devices (push subscription ids) that already received it, so a retry does not notify them twice.</summary>
  public string? DeliveredTargets { get; private set; }

  public IReadOnlyCollection<Guid> GetDeliveredTargets()
      => string.IsNullOrWhiteSpace(DeliveredTargets)
          ? []
          : DeliveredTargets
              .Split(IdSeparator, StringSplitOptions.RemoveEmptyEntries)
              .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
              .Where(id => id != Guid.Empty)
              .ToArray();

  public void MarkSent(DateTime now, IEnumerable<Guid> deliveredTargets)
  {
    Attempts++;
    RecordDelivered(deliveredTargets);
    Complete(NotificationDeliveryStatus.Sent, now, null);
  }

  public void MarkFailed(DateTime now, string? error, IEnumerable<Guid> deliveredTargets)
  {
    Attempts++;
    RecordDelivered(deliveredTargets);
    Complete(NotificationDeliveryStatus.Failed, now, error);
  }

  public void MarkSkipped(DateTime now, string reason)
      => Complete(NotificationDeliveryStatus.Skipped, now, reason);

  /// <summary>Schedules another attempt with backoff, or gives up after <see cref="MaxAttempts"/>.</summary>
  public void ScheduleRetry(DateTime now, string? error, IEnumerable<Guid> deliveredTargets)
  {
    Attempts++;
    RecordDelivered(deliveredTargets);

    var nextAttemptAt = now + RetryDelays[Math.Min(Attempts, RetryDelays.Length) - 1];
    if (Attempts >= MaxAttempts || nextAttemptAt >= ExpiresAt)
    {
      Complete(NotificationDeliveryStatus.Failed, now, error);
      return;
    }

    NextAttemptAt = DomainValidation.EnsureUtc(nextAttemptAt, nameof(nextAttemptAt));
    LastError = Truncate(error);
  }

  private void RecordDelivered(IEnumerable<Guid> deliveredTargets)
  {
    var all = GetDeliveredTargets().Concat(deliveredTargets).Distinct().ToArray();
    var joined = string.Join(IdSeparator, all);
    DeliveredTargets = joined.Length == 0 ? null : joined.Length <= 2000 ? joined : joined[..2000];
  }

  private void Complete(string status, DateTime now, string? error)
  {
    Status = status;
    CompletedAt = DomainValidation.EnsureUtc(now, nameof(now));
    LastError = Truncate(error);
  }

  private static string? Truncate(string? value)
      => string.IsNullOrWhiteSpace(value) ? null : value.Length <= 500 ? value : value[..500];
}
