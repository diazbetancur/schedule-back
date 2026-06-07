using Barbershop.Domain.Common;

namespace Barbershop.Domain.Users;

public sealed class NotificationCampaign
{
  private NotificationCampaign()
  {
  }

  public NotificationCampaign(
      string title,
      string body,
      string targetSummary,
      int recipientCount,
      Guid sentByUserId,
      DateTime createdAt)
  {
    Title = DomainValidation.Required(title, nameof(title), 160);
    Body = DomainValidation.Required(body, nameof(body), 1000);
    TargetSummary = DomainValidation.Required(targetSummary, nameof(targetSummary), 280);
    RecipientCount = DomainValidation.EnsureRange(recipientCount, 0, int.MaxValue, nameof(recipientCount));
    SentByUserId = sentByUserId;
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public string Title { get; private set; } = string.Empty;
  public string Body { get; private set; } = string.Empty;
  public string TargetSummary { get; private set; } = string.Empty;
  public int RecipientCount { get; private set; }
  public Guid SentByUserId { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public User SentByUser { get; private set; } = null!;
}
