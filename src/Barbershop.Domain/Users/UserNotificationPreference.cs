using Barbershop.Domain.Common;

namespace Barbershop.Domain.Users;

/// <summary>
/// ADR-0019 N4: a category of notifications whose push the user changed. Without a row the category is on, so only
/// the categories the user touched are stored. It never hides a notification from the bell or the history.
/// </summary>
public sealed class UserNotificationPreference
{
  private UserNotificationPreference()
  {
  }

  public UserNotificationPreference(Guid userId, string category, bool pushEnabled, DateTime updatedAt)
  {
    UserId = userId;
    Category = DomainValidation.Required(category, nameof(category), 32);
    PushEnabled = pushEnabled;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public Guid UserId { get; private set; }
  public string Category { get; private set; } = string.Empty;
  public bool PushEnabled { get; private set; }
  public DateTime UpdatedAt { get; private set; }

  public void Update(bool pushEnabled, DateTime updatedAt)
  {
    PushEnabled = pushEnabled;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }
}
