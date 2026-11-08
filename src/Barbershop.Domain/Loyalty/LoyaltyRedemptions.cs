using Barbershop.Domain.Common;

namespace Barbershop.Domain.Loyalty;

/// <summary>What a reward gives (ADR-0015 R1). There is no stock control.</summary>
public enum LoyaltyRewardKind
{
  /// <summary>A service of the catalog; delivered only by confirming the service record of whoever performed it.</summary>
  Service = 1,

  /// <summary>A product or gift described in free text; delivered from the redemptions inbox.</summary>
  Other = 2
}

/// <summary>
/// A reward of the catalog with its fixed cost in points (ADR-0015 R2). Never deleted: deactivating it only stops new
/// requests. Requests keep their own copy of name, kind, service and cost, so editing the catalog never changes them.
/// </summary>
public sealed class LoyaltyReward
{
  public const int MinNameLength = 2;
  public const int MaxNameLength = 120;
  public const int MaxDescriptionLength = 500;

  private LoyaltyReward()
  {
  }

  public LoyaltyReward(
      string name, string? description, LoyaltyRewardKind kind, Guid? serviceId, int pointsCost, Guid createdByUserId, DateTime createdAt)
  {
    Apply(name, description, kind, serviceId, pointsCost, isActive: true);
    CreatedByUserId = createdByUserId;
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public string Name { get; private set; } = string.Empty;
  public string? Description { get; private set; }
  public LoyaltyRewardKind Kind { get; private set; }

  /// <summary>Required for <see cref="LoyaltyRewardKind.Service"/>; null for <see cref="LoyaltyRewardKind.Other"/>.</summary>
  public Guid? ServiceId { get; private set; }

  public int PointsCost { get; private set; }
  public bool IsActive { get; private set; }
  public Guid CreatedByUserId { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public Guid? UpdatedByUserId { get; private set; }
  public DateTime? UpdatedAt { get; private set; }

  /// <summary>Mapped to PostgreSQL's xmin system column; used as an optimistic concurrency token.</summary>
  public uint Version { get; private set; }

  /// <summary>The audit row of the creation.</summary>
  public LoyaltyRewardChange CreationChange()
      => new(Id, LoyaltyRewardChangeType.Created, previous: null, Snapshot(), CreatedByUserId, CreatedAt);

  /// <summary>Applies the new values and returns the audit row, or null when nothing changes.</summary>
  public LoyaltyRewardChange? Update(
      string name, string? description, LoyaltyRewardKind kind, Guid? serviceId, int pointsCost, bool isActive,
      Guid changedByUserId, DateTime changedAt)
  {
    var previous = Snapshot();
    Apply(name, description, kind, serviceId, pointsCost, isActive);
    var current = Snapshot();
    if (current == previous)
    {
      return null;
    }

    UpdatedByUserId = changedByUserId;
    UpdatedAt = DomainValidation.EnsureUtc(changedAt, nameof(changedAt));
    return new LoyaltyRewardChange(Id, LoyaltyRewardChangeType.Updated, previous, current, changedByUserId, UpdatedAt.Value);
  }

  private void Apply(string name, string? description, LoyaltyRewardKind kind, Guid? serviceId, int pointsCost, bool isActive)
  {
    if (!Enum.IsDefined(kind))
    {
      throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown reward kind.");
    }

    if ((kind == LoyaltyRewardKind.Service) != (serviceId is not null && serviceId != Guid.Empty))
    {
      throw new ArgumentException("A service reward names its service; any other reward has none.", nameof(serviceId));
    }

    Name = DomainValidation.Required(name, nameof(name), MaxNameLength, MinNameLength);
    Description = DomainValidation.Optional(description, MaxDescriptionLength);
    Kind = kind;
    ServiceId = kind == LoyaltyRewardKind.Service ? serviceId : null;
    PointsCost = DomainValidation.EnsureRange(pointsCost, 1, LoyaltySettings.MaxPointsPerItem, nameof(pointsCost));
    IsActive = isActive;
  }

  private LoyaltyRewardSnapshot Snapshot() => new(Name, Description, Kind, ServiceId, PointsCost, IsActive);
}

public enum LoyaltyRewardChangeType
{
  Created = 1,
  Updated = 2
}

internal sealed record LoyaltyRewardSnapshot(
    string Name, string? Description, LoyaltyRewardKind Kind, Guid? ServiceId, int PointsCost, bool IsActive);

/// <summary>Append-only history of the catalog: who changed what and when; never edited or deleted.</summary>
public sealed class LoyaltyRewardChange
{
  private LoyaltyRewardChange()
  {
  }

  internal LoyaltyRewardChange(
      Guid rewardId, LoyaltyRewardChangeType changeType, LoyaltyRewardSnapshot? previous, LoyaltyRewardSnapshot current,
      Guid changedByUserId, DateTime changedAt)
  {
    RewardId = rewardId;
    ChangeType = changeType;
    PreviousName = previous?.Name;
    PreviousDescription = previous?.Description;
    PreviousKind = previous?.Kind;
    PreviousServiceId = previous?.ServiceId;
    PreviousPointsCost = previous?.PointsCost;
    PreviousIsActive = previous?.IsActive;
    NewName = current.Name;
    NewDescription = current.Description;
    NewKind = current.Kind;
    NewServiceId = current.ServiceId;
    NewPointsCost = current.PointsCost;
    NewIsActive = current.IsActive;
    ChangedByUserId = changedByUserId;
    ChangedAt = DomainValidation.EnsureUtc(changedAt, nameof(changedAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid RewardId { get; private set; }
  public LoyaltyRewardChangeType ChangeType { get; private set; }
  public string? PreviousName { get; private set; }
  public string? PreviousDescription { get; private set; }
  public LoyaltyRewardKind? PreviousKind { get; private set; }
  public Guid? PreviousServiceId { get; private set; }
  public int? PreviousPointsCost { get; private set; }
  public bool? PreviousIsActive { get; private set; }
  public string NewName { get; private set; } = string.Empty;
  public string? NewDescription { get; private set; }
  public LoyaltyRewardKind NewKind { get; private set; }
  public Guid? NewServiceId { get; private set; }
  public int NewPointsCost { get; private set; }
  public bool NewIsActive { get; private set; }
  public Guid ChangedByUserId { get; private set; }
  public DateTime ChangedAt { get; private set; }
}

/// <summary>Lifecycle of a redemption (ADR-0015). Requested and Approved are pending; the others are final.</summary>
public enum LoyaltyRedemptionStatus
{
  Requested = 1,
  Approved = 2,
  Delivered = 3,
  Rejected = 4,
  Cancelled = 5,
  Expired = 6
}

public enum LoyaltyRedemptionChannel
{
  /// <summary>Requested by the customer from the app.</summary>
  App = 1,

  /// <summary>Registered at the counter by staff, with the customer present and verified in person.</summary>
  Counter = 2
}

/// <summary>
/// A customer's request to exchange points for a reward (ADR-0015). Its points are reserved when it is requested,
/// soonest expiry first (<see cref="LoyaltyRedemptionAllocation"/>); a delivery consumes that reservation and a
/// rejection, cancellation or expiry releases it, exactly once. The deadlines applied are copied at request time;
/// both are inclusive Bogotá dates (start day + days − 1). Valid transitions are listed in
/// <see cref="AllowedTransitions"/>; every other change is refused.
/// </summary>
public sealed class LoyaltyRedemption
{
  public const int MinReasonLength = 3;
  public const int MaxReasonLength = 500;

  /// <summary>Every valid transition. Delivered, Rejected, Cancelled and Expired are final.</summary>
  public static readonly IReadOnlyDictionary<LoyaltyRedemptionStatus, IReadOnlySet<LoyaltyRedemptionStatus>> AllowedTransitions =
      new Dictionary<LoyaltyRedemptionStatus, IReadOnlySet<LoyaltyRedemptionStatus>>
      {
        [LoyaltyRedemptionStatus.Requested] = new HashSet<LoyaltyRedemptionStatus>
        {
          LoyaltyRedemptionStatus.Approved, LoyaltyRedemptionStatus.Rejected, LoyaltyRedemptionStatus.Cancelled, LoyaltyRedemptionStatus.Expired
        },
        [LoyaltyRedemptionStatus.Approved] = new HashSet<LoyaltyRedemptionStatus>
        {
          LoyaltyRedemptionStatus.Delivered, LoyaltyRedemptionStatus.Cancelled, LoyaltyRedemptionStatus.Expired
        },
        [LoyaltyRedemptionStatus.Delivered] = new HashSet<LoyaltyRedemptionStatus>(),
        [LoyaltyRedemptionStatus.Rejected] = new HashSet<LoyaltyRedemptionStatus>(),
        [LoyaltyRedemptionStatus.Cancelled] = new HashSet<LoyaltyRedemptionStatus>(),
        [LoyaltyRedemptionStatus.Expired] = new HashSet<LoyaltyRedemptionStatus>(),
      };

  private LoyaltyRedemption()
  {
  }

  public LoyaltyRedemption(
      Guid customerId,
      LoyaltyReward reward,
      string? serviceNameSnapshot,
      LoyaltyRedemptionChannel channel,
      bool inPersonVerified,
      Guid idempotencyKey,
      Guid requestedByUserId,
      DateTime requestedAt,
      DateOnly requestedOn,
      int approvalDays,
      int deliveryDays)
  {
    if (idempotencyKey == Guid.Empty)
    {
      throw new ArgumentException("The idempotency key is required.", nameof(idempotencyKey));
    }

    if (channel == LoyaltyRedemptionChannel.Counter && !inPersonVerified)
    {
      throw new ArgumentException("A counter redemption needs the in-person verification.", nameof(inPersonVerified));
    }

    if (reward.Kind == LoyaltyRewardKind.Service && string.IsNullOrWhiteSpace(serviceNameSnapshot))
    {
      throw new ArgumentException("A service reward keeps the name of its service.", nameof(serviceNameSnapshot));
    }

    CustomerId = customerId;
    RewardId = reward.Id;
    RewardNameSnapshot = reward.Name;
    RewardKindSnapshot = reward.Kind;
    ServiceIdSnapshot = reward.ServiceId;
    ServiceNameSnapshot = reward.Kind == LoyaltyRewardKind.Service
        ? DomainValidation.Required(serviceNameSnapshot, nameof(serviceNameSnapshot), 120)
        : null;
    PointsCost = reward.PointsCost;
    Channel = channel;
    InPersonVerified = inPersonVerified;
    IdempotencyKey = idempotencyKey;
    RequestedByUserId = requestedByUserId;
    RequestedAt = DomainValidation.EnsureUtc(requestedAt, nameof(requestedAt));
    RequestedOn = requestedOn;
    ApprovalDays = DomainValidation.EnsureRange(approvalDays, LoyaltySettings.MinRedemptionDays, LoyaltySettings.MaxRedemptionDays, nameof(approvalDays));
    DeliveryDays = DomainValidation.EnsureRange(deliveryDays, LoyaltySettings.MinRedemptionDays, LoyaltySettings.MaxRedemptionDays, nameof(deliveryDays));
    ApproveBy = requestedOn.AddDays(ApprovalDays - 1);
    Status = LoyaltyRedemptionStatus.Requested;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();

  /// <summary>The live profile when it was requested. Pending redemptions block merges, so it stays live meanwhile.</summary>
  public Guid CustomerId { get; private set; }

  public Guid RewardId { get; private set; }

  // Copied at request time: editing or deactivating the reward never changes them.
  public string RewardNameSnapshot { get; private set; } = string.Empty;
  public LoyaltyRewardKind RewardKindSnapshot { get; private set; }
  public Guid? ServiceIdSnapshot { get; private set; }
  public string? ServiceNameSnapshot { get; private set; }
  public int PointsCost { get; private set; }

  public LoyaltyRedemptionChannel Channel { get; private set; }
  public bool InPersonVerified { get; private set; }
  public Guid IdempotencyKey { get; private set; }

  public LoyaltyRedemptionStatus Status { get; private set; }

  public Guid RequestedByUserId { get; private set; }
  public DateTime RequestedAt { get; private set; }

  /// <summary>Bogotá date of the request: first day of the approval deadline.</summary>
  public DateOnly RequestedOn { get; private set; }

  /// <summary>Deadlines applied, copied from the settings at request time.</summary>
  public int ApprovalDays { get; private set; }

  public int DeliveryDays { get; private set; }

  /// <summary>Last day (inclusive, Bogotá) to approve: RequestedOn + ApprovalDays − 1.</summary>
  public DateOnly ApproveBy { get; private set; }

  public Guid? ApprovedByUserId { get; private set; }
  public DateTime? ApprovedAt { get; private set; }

  /// <summary>Bogotá date of the approval: first day of the delivery deadline.</summary>
  public DateOnly? ApprovedOn { get; private set; }

  /// <summary>Last day (inclusive, Bogotá) to deliver: ApprovedOn + DeliveryDays − 1.</summary>
  public DateOnly? DeliverBy { get; private set; }

  public Guid? DeliveredByUserId { get; private set; }
  public DateTime? DeliveredAt { get; private set; }

  /// <summary>For a service reward: the confirmed service record that delivered it.</summary>
  public Guid? ServiceRecordId { get; private set; }

  public Guid? RejectedByUserId { get; private set; }
  public DateTime? RejectedAt { get; private set; }
  public string? RejectionReason { get; private set; }

  public Guid? CancelledByUserId { get; private set; }
  public DateTime? CancelledAt { get; private set; }
  public string? CancellationReason { get; private set; }

  /// <summary>True when the customer cancelled it themselves.</summary>
  public bool CancelledByCustomer { get; private set; }

  public DateTime? ExpiredAt { get; private set; }

  /// <summary>Mapped to PostgreSQL's xmin system column; used as an optimistic concurrency token.</summary>
  public uint Version { get; private set; }

  public bool IsPending => IsPendingStatus(Status);

  public static bool IsPendingStatus(LoyaltyRedemptionStatus status)
      => status is LoyaltyRedemptionStatus.Requested or LoyaltyRedemptionStatus.Approved;

  /// <summary>The last day of the deadline that applies to the current status, or null when it is final.</summary>
  public DateOnly? CurrentDeadline => Status switch
  {
    LoyaltyRedemptionStatus.Requested => ApproveBy,
    LoyaltyRedemptionStatus.Approved => DeliverBy,
    _ => null
  };

  /// <summary>Pending and past its deadline on <paramref name="today"/> (Bogotá), even if the job has not recorded it yet.</summary>
  public bool IsPastDeadlineOn(DateOnly today) => CurrentDeadline is { } deadline && today > deadline;

  public void Approve(Guid approvedByUserId, DateTime approvedAt, DateOnly today)
  {
    EnsureTransition(LoyaltyRedemptionStatus.Approved);
    EnsureWithinDeadline(today);
    ApprovedByUserId = approvedByUserId;
    ApprovedAt = DomainValidation.EnsureUtc(approvedAt, nameof(approvedAt));
    ApprovedOn = today;
    DeliverBy = today.AddDays(DeliveryDays - 1);
    Status = LoyaltyRedemptionStatus.Approved;
  }

  public void Reject(string reason, Guid rejectedByUserId, DateTime rejectedAt)
  {
    EnsureTransition(LoyaltyRedemptionStatus.Rejected);
    RejectionReason = DomainValidation.Required(reason, nameof(reason), MaxReasonLength, MinReasonLength);
    RejectedByUserId = rejectedByUserId;
    RejectedAt = DomainValidation.EnsureUtc(rejectedAt, nameof(rejectedAt));
    Status = LoyaltyRedemptionStatus.Rejected;
  }

  /// <summary>Staff must give a reason; the customer may cancel their own without one.</summary>
  public void Cancel(string? reason, Guid cancelledByUserId, bool byCustomer, DateTime cancelledAt)
  {
    EnsureTransition(LoyaltyRedemptionStatus.Cancelled);
    CancellationReason = byCustomer
        ? DomainValidation.Optional(reason, MaxReasonLength)
        : DomainValidation.Required(reason, nameof(reason), MaxReasonLength, MinReasonLength);
    CancelledByUserId = cancelledByUserId;
    CancelledByCustomer = byCustomer;
    CancelledAt = DomainValidation.EnsureUtc(cancelledAt, nameof(cancelledAt));
    Status = LoyaltyRedemptionStatus.Cancelled;
  }

  /// <summary>Only once its current deadline has passed.</summary>
  public void Expire(DateTime expiredAt, DateOnly today)
  {
    EnsureTransition(LoyaltyRedemptionStatus.Expired);
    if (!IsPastDeadlineOn(today))
    {
      throw new InvalidOperationException("The redemption is still within its deadline.");
    }

    ExpiredAt = DomainValidation.EnsureUtc(expiredAt, nameof(expiredAt));
    Status = LoyaltyRedemptionStatus.Expired;
  }

  /// <summary>
  /// A service reward is delivered only through its confirmed service record (<paramref name="serviceRecordId"/>);
  /// any other reward without one.
  /// </summary>
  public void Deliver(Guid deliveredByUserId, DateTime deliveredAt, DateOnly today, Guid? serviceRecordId)
  {
    EnsureTransition(LoyaltyRedemptionStatus.Delivered);
    if ((RewardKindSnapshot == LoyaltyRewardKind.Service) != (serviceRecordId is not null))
    {
      throw new InvalidOperationException(RewardKindSnapshot == LoyaltyRewardKind.Service
          ? "A service reward is delivered only by confirming its service record."
          : "Only a service reward is delivered through a service record.");
    }

    EnsureWithinDeadline(today);
    DeliveredByUserId = deliveredByUserId;
    DeliveredAt = DomainValidation.EnsureUtc(deliveredAt, nameof(deliveredAt));
    ServiceRecordId = serviceRecordId;
    Status = LoyaltyRedemptionStatus.Delivered;
  }

  private void EnsureTransition(LoyaltyRedemptionStatus target)
  {
    if (!AllowedTransitions[Status].Contains(target))
    {
      throw new InvalidOperationException($"A {Status} redemption cannot become {target}.");
    }
  }

  private void EnsureWithinDeadline(DateOnly today)
  {
    if (IsPastDeadlineOn(today))
    {
      throw new InvalidOperationException($"The redemption is past its deadline ({CurrentDeadline:yyyy-MM-dd}).");
    }
  }
}

/// <summary>How many points a redemption reserved from one lot; a release gives back exactly this, once.</summary>
public sealed class LoyaltyRedemptionAllocation
{
  private LoyaltyRedemptionAllocation()
  {
  }

  public LoyaltyRedemptionAllocation(Guid redemptionId, Guid lotId, int points)
  {
    RedemptionId = redemptionId;
    LotId = lotId;
    Points = DomainValidation.EnsureRange(points, 1, int.MaxValue, nameof(points));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid RedemptionId { get; private set; }
  public Guid LotId { get; private set; }
  public int Points { get; private set; }
}
