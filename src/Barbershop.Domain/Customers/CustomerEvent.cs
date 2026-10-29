using Barbershop.Domain.Common;

namespace Barbershop.Domain.Customers;

/// <summary>Append-only audit record of account links and profile merges.</summary>
public sealed class CustomerEvent
{
  private CustomerEvent()
  {
  }

  public CustomerEvent(
      Guid customerId,
      CustomerEventType type,
      Guid performedByUserId,
      DateTime performedAt,
      Guid? userId = null,
      Guid? relatedCustomerId = null,
      string? reason = null)
  {
    CustomerId = customerId;
    Type = type;
    PerformedByUserId = performedByUserId;
    PerformedAt = DomainValidation.EnsureUtc(performedAt, nameof(performedAt));
    UserId = userId;
    RelatedCustomerId = relatedCustomerId;
    Reason = DomainValidation.Optional(reason, 500);
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid CustomerId { get; private set; }
  public CustomerEventType Type { get; private set; }
  public Guid? UserId { get; private set; }
  public Guid? RelatedCustomerId { get; private set; }
  public string? Reason { get; private set; }
  public Guid PerformedByUserId { get; private set; }
  public DateTime PerformedAt { get; private set; }
}

/// <summary>Values follow the lifecycle order; the history uses it to order events that share a timestamp.</summary>
public enum CustomerEventType
{
  Created = 1,
  AccountLinked = 2,
  AccountUnlinked = 3,
  MergedFrom = 4,
  MergedInto = 5
}
