using Barbershop.Domain.Common;

namespace Barbershop.Domain.Bills;

/// <summary>
/// Audit trail of a bill (ADR-0020, C3): append-only (database triggers reject updates and deletes). Each change keeps
/// who, when, the status the bill had, an optional reason and the before and after of what changed, as JSON.
/// </summary>
public sealed class BillEvent
{
  private BillEvent()
  {
  }

  public BillEvent(
      Guid billId, BillEventType type, BillStatus statusAtEvent, Guid userId, DateTime createdAt, string? reason = null,
      string? beforeJson = null, string? afterJson = null)
  {
    BillId = billId;
    Type = type;
    StatusAtEvent = statusAtEvent;
    UserId = userId;
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
    Reason = DomainValidation.Optional(reason, Bill.MaxReasonLength);
    BeforeJson = beforeJson;
    AfterJson = afterJson;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid BillId { get; private set; }
  public BillEventType Type { get; private set; }

  /// <summary>Lets the history tell an edit while building the bill from an audited edit by the approver.</summary>
  public BillStatus StatusAtEvent { get; private set; }

  public Guid UserId { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public string? Reason { get; private set; }
  public string? BeforeJson { get; private set; }
  public string? AfterJson { get; private set; }
}

public enum BillEventType
{
  Opened = 1,
  LineAdded = 2,
  LineChanged = 3,
  LineRemoved = 4,
  HeaderChanged = 5,
  Closed = 6,
  PaymentMethodChanged = 7,
  Approved = 8,
  Voided = 9,
  AppointmentDetached = 10,
  Unapproved = 11
}

/// <summary>
/// One approval cycle of a bill (ADR-0020 §3). It keeps its own copy of the lines as approved (with the share of the
/// discounts and the points decided), so a later correction never loses what was approved before.
/// </summary>
public sealed class BillApproval
{
  private BillApproval()
  {
  }

  public BillApproval(
      Guid billId, int sequence, Guid idempotencyKey, Guid approvedByUserId, DateTime approvedAt, bool isSelfApproved,
      Guid? customerId, string linesJson, string? notes, Guid? id = null)
  {
    Id = id ?? Guid.NewGuid();
    if (idempotencyKey == Guid.Empty)
    {
      throw new ArgumentException("An approval needs an idempotency key.", nameof(idempotencyKey));
    }

    BillId = billId;
    Sequence = DomainValidation.EnsureRange(sequence, 1, int.MaxValue, nameof(sequence));
    IdempotencyKey = idempotencyKey;
    ApprovedByUserId = approvedByUserId;
    ApprovedAt = DomainValidation.EnsureUtc(approvedAt, nameof(approvedAt));
    IsSelfApproved = isSelfApproved;
    CustomerId = customerId;
    LinesJson = linesJson;
    Notes = DomainValidation.Optional(notes, Bill.MaxNotesLength);
  }

  public Guid Id { get; private set; }
  public Guid BillId { get; private set; }
  public int Sequence { get; private set; }
  public Guid IdempotencyKey { get; private set; }
  public Guid ApprovedByUserId { get; private set; }
  public DateTime ApprovedAt { get; private set; }
  public bool IsSelfApproved { get; private set; }

  /// <summary>The live customer profile the postings used (ADR-0013 D6); null for a walk-in customer.</summary>
  public Guid? CustomerId { get; private set; }

  public string LinesJson { get; private set; } = "[]";
  public string? Notes { get; private set; }

  // ADR-0020 stage 4: the unapproval of this cycle (its postings were reversed).
  public DateTime? UnapprovedAt { get; private set; }
  public Guid? UnapprovedByUserId { get; private set; }
  public string? UnapprovalReason { get; private set; }

  /// <summary>Sent by the client: a retried unapproval returns the stored one.</summary>
  public Guid? UnapprovalKey { get; private set; }

  /// <summary>ADR-0018 D1 applied to the unapproval: an Admin who is the customer or a professional of the bill.</summary>
  public bool IsSelfUnapproved { get; private set; }

  public bool IsUnapproved => UnapprovedAt is not null;

  public void Unapprove(Guid unapprovalKey, string reason, Guid unapprovedByUserId, DateTime utcNow, bool selfUnapproved)
  {
    if (IsUnapproved)
    {
      throw new InvalidOperationException("This approval was already unapproved.");
    }

    if (unapprovalKey == Guid.Empty)
    {
      throw new ArgumentException("An unapproval needs an idempotency key.", nameof(unapprovalKey));
    }

    UnapprovalKey = unapprovalKey;
    UnapprovalReason = DomainValidation.Required(reason?.Trim(), nameof(reason), Bill.MaxReasonLength, Bill.MinReasonLength);
    UnapprovedByUserId = unapprovedByUserId;
    UnapprovedAt = DomainValidation.EnsureUtc(utcNow, nameof(utcNow));
    IsSelfUnapproved = selfUnapproved;
  }
}

/// <summary>
/// One effect of an approval on one line: the income, consumption, delivered redemption, points lot or stock movement it
/// created. A unique index keeps every effect from being created twice.
/// </summary>
public sealed class BillPosting
{
  private BillPosting()
  {
  }

  public BillPosting(Guid id, Guid approvalId, Guid billLineId, BillPostingEffect effect, Guid targetId, DateTime createdAt)
  {
    Id = id;
    ApprovalId = approvalId;
    BillLineId = billLineId;
    Effect = effect;
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
    switch (effect)
    {
      case BillPostingEffect.Income:
        IncomeEntryId = targetId;
        break;
      case BillPostingEffect.MembershipConsumption:
        MembershipConsumptionId = targetId;
        break;
      case BillPostingEffect.LoyaltyAccrual:
        LoyaltyLotId = targetId;
        break;
      case BillPostingEffect.RedemptionDelivery:
        LoyaltyRedemptionId = targetId;
        break;
      case BillPostingEffect.InventorySale:
        InventoryMovementId = targetId;
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(effect));
    }
  }

  public Guid Id { get; private set; }
  public Guid ApprovalId { get; private set; }
  public Guid BillLineId { get; private set; }
  public BillPostingEffect Effect { get; private set; }
  public Guid? IncomeEntryId { get; private set; }
  public Guid? MembershipConsumptionId { get; private set; }
  public Guid? LoyaltyLotId { get; private set; }
  public Guid? LoyaltyRedemptionId { get; private set; }
  public Guid? InventoryMovementId { get; private set; }
  public DateTime CreatedAt { get; private set; }

  /// <summary>
  /// ADR-0020 stage 4: when the approval was unapproved, what reversed this effect: the counter-entry income, the
  /// consumption reversal, the debt (or the lot) of the points, the redemption reopened or the stock return movement.
  /// </summary>
  public DateTime? ReversedAt { get; private set; }
  public Guid? ReversalTargetId { get; private set; }

  public bool IsReversed => ReversedAt is not null;

  public void MarkReversed(Guid? reversalTargetId, DateTime utcNow)
  {
    if (IsReversed)
    {
      throw new InvalidOperationException("This posting was already reversed.");
    }

    ReversalTargetId = reversalTargetId;
    ReversedAt = DomainValidation.EnsureUtc(utcNow, nameof(utcNow));
  }
}

public enum BillPostingEffect
{
  Income = 1,
  MembershipConsumption = 2,
  LoyaltyAccrual = 3,
  RedemptionDelivery = 4,
  InventorySale = 5
}

/// <summary>Q1: payment methods are a list the administrator keeps; a method is deactivated, never deleted.</summary>
public sealed class PaymentMethod
{
  public const int MinNameLength = 2;
  public const int MaxNameLength = 60;

  private PaymentMethod()
  {
  }

  public PaymentMethod(string name, int sortOrder, DateTime createdAt)
  {
    Name = DomainValidation.Required(name?.Trim(), nameof(name), MaxNameLength, MinNameLength);
    SortOrder = DomainValidation.EnsureRange(sortOrder, 0, 9_999, nameof(sortOrder));
    IsActive = true;
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public string Name { get; private set; } = string.Empty;
  public int SortOrder { get; private set; }
  public bool IsActive { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }

  public void Update(string name, int sortOrder, bool isActive, DateTime utcNow)
  {
    Name = DomainValidation.Required(name?.Trim(), nameof(name), MaxNameLength, MinNameLength);
    SortOrder = DomainValidation.EnsureRange(sortOrder, 0, 9_999, nameof(sortOrder));
    IsActive = isActive;
    UpdatedAt = DomainValidation.EnsureUtc(utcNow, nameof(utcNow));
  }
}

/// <summary>
/// Q7: the consecutive of the bills, one continuous series without yearly reset. Closing locks the row (FOR UPDATE) in
/// its own transaction, so numbers have no gaps: a rolled back close never spends one.
/// </summary>
public sealed class BillNumberSeries
{
  public const string DefaultPrefix = "C";

  private BillNumberSeries()
  {
  }

  public string Prefix { get; private set; } = DefaultPrefix;
  public int NextNumber { get; private set; } = 1;
  public DateTime? UpdatedAt { get; private set; }

  public int Take(DateTime utcNow)
  {
    var number = NextNumber;
    NextNumber = checked(NextNumber + 1);
    UpdatedAt = DomainValidation.EnsureUtc(utcNow, nameof(utcNow));
    return number;
  }
}
