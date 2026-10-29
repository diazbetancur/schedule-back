using Barbershop.Domain.Common;

namespace Barbershop.Domain.Memberships;

/// <summary>
/// A balance line of a sold membership: a granted quantity usable by one or more services. A pool plan has
/// one purchased line covering all its services; a per-service plan has one purchased line per service;
/// every service gift has a line of its own so gifted units stay separate from purchased ones.
/// </summary>
public sealed class MembershipAllowance
{
  private MembershipAllowance()
  {
  }

  public MembershipAllowance(
      Guid customerMembershipId,
      MembershipAllowanceOrigin origin,
      int grantedQuantity,
      int position,
      Guid? membershipGiftId = null)
  {
    if (origin == MembershipAllowanceOrigin.Gift && membershipGiftId is null)
    {
      throw new ArgumentException("A gift allowance belongs to a gift.", nameof(membershipGiftId));
    }

    CustomerMembershipId = customerMembershipId;
    Origin = origin;
    GrantedQuantity = DomainValidation.EnsureRange(grantedQuantity, 1, MembershipPlan.MaxQuantity, nameof(grantedQuantity));
    Position = DomainValidation.EnsureRange(position, 0, int.MaxValue, nameof(position));
    MembershipGiftId = membershipGiftId;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid CustomerMembershipId { get; private set; }
  public MembershipAllowanceOrigin Origin { get; private set; }
  public Guid? MembershipGiftId { get; private set; }
  public int GrantedQuantity { get; private set; }
  public int Position { get; private set; }
  public ICollection<MembershipAllowanceService> Services { get; } = [];
}

/// <summary>A service an allowance can be used for, with its name as it was at sale time.</summary>
public sealed class MembershipAllowanceService
{
  private MembershipAllowanceService()
  {
  }

  public MembershipAllowanceService(Guid membershipAllowanceId, Guid serviceId, string serviceNameSnapshot)
  {
    MembershipAllowanceId = membershipAllowanceId;
    ServiceId = serviceId;
    ServiceNameSnapshot = DomainValidation.Required(serviceNameSnapshot, nameof(serviceNameSnapshot), 120, 1);
  }

  public Guid MembershipAllowanceId { get; private set; }
  public Guid ServiceId { get; private set; }
  public string ServiceNameSnapshot { get; private set; } = string.Empty;
}

/// <summary>
/// A concrete gift granted to a sold membership by one of its plan's gift rules. Service gifts get their
/// own allowance; physical gifts stay pending delivery (delivery is recorded in a later stage).
/// </summary>
public sealed class MembershipGift
{
  private MembershipGift()
  {
  }

  public MembershipGift(Guid customerMembershipId, MembershipPlanGiftRule rule, string? serviceNameSnapshot, DateTime createdAt)
  {
    CustomerMembershipId = customerMembershipId;
    MembershipPlanGiftRuleId = rule.Id;
    Kind = rule.Kind;
    Condition = rule.Condition;
    Quantity = rule.Quantity;
    Position = rule.Position;
    ServiceId = rule.ServiceId;
    ServiceNameSnapshot = rule.Kind == MembershipGiftKind.Service
        ? DomainValidation.Required(serviceNameSnapshot, nameof(serviceNameSnapshot), 120, 1)
        : null;
    DescriptionSnapshot = rule.Description;
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid CustomerMembershipId { get; private set; }

  /// <summary>The catalog rule it came from (rules are only soft-deleted, so this stays valid).</summary>
  public Guid MembershipPlanGiftRuleId { get; private set; }

  public MembershipGiftKind Kind { get; private set; }
  public MembershipGiftCondition Condition { get; private set; }
  public int Quantity { get; private set; }
  public int Position { get; private set; }
  public Guid? ServiceId { get; private set; }
  public string? ServiceNameSnapshot { get; private set; }
  public string? DescriptionSnapshot { get; private set; }
  public DateTime CreatedAt { get; private set; }
}

/// <summary>Append-only audit record of what happened to a sold membership.</summary>
public sealed class MembershipEvent
{
  private MembershipEvent()
  {
  }

  public MembershipEvent(
      Guid customerMembershipId,
      MembershipEventType type,
      Guid performedByUserId,
      DateTime performedAt,
      string? reason = null,
      Guid? incomeEntryId = null,
      Guid? relatedCustomerId = null)
  {
    CustomerMembershipId = customerMembershipId;
    Type = type;
    PerformedByUserId = performedByUserId;
    PerformedAt = DomainValidation.EnsureUtc(performedAt, nameof(performedAt));
    Reason = DomainValidation.Optional(reason, 500);
    IncomeEntryId = incomeEntryId;
    RelatedCustomerId = relatedCustomerId;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid CustomerMembershipId { get; private set; }
  public MembershipEventType Type { get; private set; }

  /// <summary>Annulment reason, expired-activation reason or income-discrepancy justification.</summary>
  public string? Reason { get; private set; }

  public Guid? IncomeEntryId { get; private set; }
  public Guid? RelatedCustomerId { get; private set; }
  public Guid PerformedByUserId { get; private set; }
  public DateTime PerformedAt { get; private set; }
}

public enum MembershipAllowanceOrigin
{
  Purchased = 1,
  Gift = 2
}

/// <summary>Values follow the lifecycle order; the history uses it to order events that share a timestamp.</summary>
public enum MembershipEventType
{
  Sold = 1,
  Activated = 2,

  /// <summary>Exceptional activation of a membership whose end date had already passed; carries a reason.</summary>
  ActivatedAfterExpiry = 3,

  IncomeLinked = 4,
  IncomeUnlinked = 5,
  MovedByMerge = 6,
  Annulled = 7
}
