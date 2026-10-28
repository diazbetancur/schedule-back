using Barbershop.Domain.Common;

namespace Barbershop.Domain.Memberships;

/// <summary>
/// A gift configured on a plan. It is only a rule: the concrete gift belongs to a sold membership
/// and is created at sale time. Removed rules are soft-deleted so sold gifts can keep pointing at them.
/// </summary>
public sealed class MembershipPlanGiftRule
{
  private MembershipPlanGiftRule()
  {
  }

  public MembershipPlanGiftRule(
      Guid membershipPlanId,
      MembershipGiftKind kind,
      Guid? serviceId,
      string? description,
      int quantity,
      MembershipGiftCondition condition,
      int position,
      DateTime createdAt)
  {
    MembershipPlanId = membershipPlanId;
    Set(kind, serviceId, description, quantity, condition, position);
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid MembershipPlanId { get; private set; }
  public MembershipGiftKind Kind { get; private set; }

  /// <summary>The gifted service, for <see cref="MembershipGiftKind.Service"/> gifts.</summary>
  public Guid? ServiceId { get; private set; }

  /// <summary>Free-text item, for <see cref="MembershipGiftKind.Physical"/> gifts.</summary>
  public string? Description { get; private set; }

  public int Quantity { get; private set; }
  public MembershipGiftCondition Condition { get; private set; }

  /// <summary>Display order within the plan, as the admin configured it.</summary>
  public int Position { get; private set; }

  public bool IsDeleted { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }

  public void Update(
      MembershipGiftKind kind,
      Guid? serviceId,
      string? description,
      int quantity,
      MembershipGiftCondition condition,
      int position,
      DateTime updatedAt)
  {
    Set(kind, serviceId, description, quantity, condition, position);
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public void MarkDeleted(DateTime updatedAt)
  {
    IsDeleted = true;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  private void Set(
      MembershipGiftKind kind,
      Guid? serviceId,
      string? description,
      int quantity,
      MembershipGiftCondition condition,
      int position)
  {
    if (!Enum.IsDefined(kind) || !Enum.IsDefined(condition))
    {
      throw new ArgumentOutOfRangeException(nameof(kind), "Unknown gift kind or condition.");
    }

    if (kind == MembershipGiftKind.Service)
    {
      ServiceId = serviceId ?? throw new ArgumentException("A service gift needs a service.", nameof(serviceId));
      Description = null;
    }
    else
    {
      ServiceId = null;
      Description = DomainValidation.Required(description, nameof(description), 200, 2);
    }

    Kind = kind;
    Quantity = DomainValidation.EnsureRange(quantity, 1, MembershipPlan.MaxQuantity, nameof(quantity));
    Condition = condition;
    Position = DomainValidation.EnsureRange(position, 0, int.MaxValue, nameof(position));
  }
}

public enum MembershipGiftKind
{
  /// <summary>Extra units of one of the plan's services, with a balance of their own.</summary>
  Service = 1,

  /// <summary>A physical item described in free text; there is no product catalog or inventory.</summary>
  Physical = 2
}

public enum MembershipGiftCondition
{
  /// <summary>Granted on every purchase of the plan.</summary>
  EveryPurchase = 1,

  /// <summary>Granted only on the customer's first non-annulled purchase of this plan.</summary>
  FirstPurchaseOfPlan = 2
}
