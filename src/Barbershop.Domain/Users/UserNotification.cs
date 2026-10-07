using Barbershop.Domain.Common;

namespace Barbershop.Domain.Users;

/// <summary>A notification kept in a user's inbox (the bell), independent of whether a push was delivered.</summary>
public sealed class UserNotification
{
  private UserNotification()
  {
  }

  public UserNotification(
      Guid userId,
      string type,
      string title,
      string body,
      string? url,
      DateTime createdAt,
      string? dedupKey = null)
  {
    UserId = userId;
    Type = DomainValidation.Required(type, nameof(type), 64);
    Title = DomainValidation.Required(title, nameof(title), 160);
    Body = DomainValidation.Required(body, nameof(body), 1000);
    Url = DomainValidation.Optional(url, 512);
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
    DedupKey = DomainValidation.Optional(dedupKey, 200);
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid UserId { get; private set; }
  public string Type { get; private set; } = string.Empty;
  public string Title { get; private set; } = string.Empty;
  public string Body { get; private set; } = string.Empty;
  public string? Url { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? ReadAt { get; private set; }

  /// <summary>Unique per user when set (e.g. "reminder-24h:{appointment}:{start}:{user}"), so scheduled notifications are created once.</summary>
  public string? DedupKey { get; private set; }

  public void MarkAsRead(DateTime readAt)
  {
    ReadAt ??= DomainValidation.EnsureUtc(readAt, nameof(readAt));
  }
}
