using Barbershop.Domain.Common;

namespace Barbershop.Domain.Bills;

/// <summary>
/// ADR-0020: the bill (internal receipt, not an electronic invoice) of what a customer took and paid, opened from an
/// appointment or without one. It is built while <see cref="BillStatus.Open"/>, closed and charged in one step (it then
/// gets its gapless number and its single payment method), and an approver posts it: incomes, commissions, membership
/// consumptions, redemptions, points and stock. Every change is audited in <see cref="BillEvent"/>; the money columns are
/// projections of the lines, recomputed on every change.
/// </summary>
public sealed class Bill
{
  public const int MaxLines = 40;
  public const int MaxNotesLength = 500;
  public const int MinReasonLength = 3;
  public const int MaxReasonLength = 500;
  public const int MinCustomerNameLength = 2;
  public const int MaxCustomerNameLength = 120;

  /// <summary>Q5: an open bill older than this shows as overdue in the pending view; it never closes or voids by itself.</summary>
  public const int OverdueAfterDays = 7;

  private readonly List<BillLine> _lines = [];

  private Bill()
  {
  }

  public Bill(
      Guid idempotencyKey,
      Guid? appointmentId,
      Guid? customerId,
      string customerNameSnapshot,
      Guid? staffProfileId,
      DateOnly serviceDate,
      string? notes,
      Guid openedByUserId,
      DateTime openedAt)
  {
    if (idempotencyKey == Guid.Empty)
    {
      throw new ArgumentException("A bill needs an idempotency key.", nameof(idempotencyKey));
    }

    IdempotencyKey = idempotencyKey;
    AppointmentId = appointmentId;
    CustomerId = customerId;
    CustomerNameSnapshot = DomainValidation.Required(
        customerNameSnapshot, nameof(customerNameSnapshot), MaxCustomerNameLength, MinCustomerNameLength);
    StaffProfileId = staffProfileId;
    ServiceDate = serviceDate;
    Notes = DomainValidation.Optional(notes, MaxNotesLength);
    OpenedByUserId = openedByUserId;
    OpenedAt = DomainValidation.EnsureUtc(openedAt, nameof(openedAt));
    Status = BillStatus.Open;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();

  /// <summary>Sent by the client when opening; opening again with the same key returns this bill.</summary>
  public Guid IdempotencyKey { get; private set; }

  /// <summary>Prefix of the number series (Q7: one series, "C"); set when the bill is closed.</summary>
  public string? Series { get; private set; }

  /// <summary>Gapless consecutive number, assigned when closing (an abandoned open bill never spends one).</summary>
  public int? Number { get; private set; }

  public BillStatus Status { get; private set; }

  /// <summary>Optional; at most one bill that is not voided per appointment (partial unique index).</summary>
  public Guid? AppointmentId { get; private set; }

  /// <summary>The customer profile (P8); null for a walk-in customer known only by name (Q4).</summary>
  public Guid? CustomerId { get; private set; }

  public string CustomerNameSnapshot { get; private set; } = string.Empty;

  /// <summary>The professional the bill belongs to (the appointment's, or the professional who opened it); null at the counter.</summary>
  public Guid? StaffProfileId { get; private set; }

  /// <summary>Bogotá date of the service: the date of the incomes and consumptions it posts.</summary>
  public DateOnly ServiceDate { get; private set; }

  public string? Notes { get; private set; }

  public Guid OpenedByUserId { get; private set; }
  public DateTime OpenedAt { get; private set; }
  public Guid? ClosedByUserId { get; private set; }
  public DateTime? ClosedAt { get; private set; }

  /// <summary>P6: one payment method per bill, recorded when closing; none when nothing is charged.</summary>
  public Guid? PaymentMethodId { get; private set; }
  public string? PaymentMethodNameSnapshot { get; private set; }

  /// <summary>
  /// SONDA BUG-121: what was charged when the bill was closed (total and payment method). Corrections of the closed bill
  /// (lines, payment method) change <see cref="Total"/> and <see cref="PaymentMethodId"/>, never these: the cash closing of
  /// a past day is fixed. Null on bills closed before the copy existed whose charge could not be rebuilt from their events.
  /// </summary>
  public int? ChargedTotal { get; private set; }
  public Guid? ChargedPaymentMethodId { get; private set; }
  public string? ChargedPaymentMethodName { get; private set; }

  /// <summary>Services (not courtesies) and products at their catalog prices.</summary>
  public int Subtotal { get; private set; }

  public int DiscountTotal { get; private set; }

  /// <summary>Tips charged with the bill.</summary>
  public int TipTotal { get; private set; }

  /// <summary>Tips paid straight to the professional (QR or in hand): recorded, not charged.</summary>
  public int DirectTipTotal { get; private set; }

  /// <summary>What the customer pays with the payment method: subtotal − discounts + tips charged with the bill.</summary>
  public int Total { get; private set; }

  public Guid? CurrentApprovalId { get; private set; }
  public Guid? ApprovedByUserId { get; private set; }
  public DateTime? ApprovedAt { get; private set; }

  /// <summary>P4 / ADR-0018 D1: approved by an Admin who is the customer or a professional of the bill.</summary>
  public bool IsSelfApproved { get; private set; }

  public Guid? VoidedByUserId { get; private set; }
  public DateTime? VoidedAt { get; private set; }
  public string? VoidReason { get; private set; }

  public DateTime? UpdatedAt { get; private set; }

  /// <summary>Mapped to PostgreSQL's xmin system column; used as an optimistic concurrency token.</summary>
  public uint Version { get; private set; }

  public IReadOnlyList<BillLine> Lines => _lines;

  public string? DisplayNumber => Number is { } number ? FormatNumber(Series ?? BillNumberSeries.DefaultPrefix, number) : null;

  public bool IsWalkIn => CustomerId is null;

  public static string FormatNumber(string series, int number) => $"{series}-{number:D6}";

  public bool IsOverdueOn(DateTime utcNow) => Status == BillStatus.Open && OpenedAt.AddDays(OverdueAfterDays) <= utcNow;

  public BillLine AddLine(BillLine line, DateTime utcNow)
  {
    EnsureEditable();
    if (_lines.Count >= MaxLines)
    {
      throw new InvalidOperationException($"A bill holds at most {MaxLines} lines.");
    }

    line.AttachTo(Id, _lines.Count == 0 ? 0 : _lines.Max(existing => existing.Position) + 1);
    _lines.Add(line);
    Touch(utcNow);
    return line;
  }

  public void RemoveLine(BillLine line, DateTime utcNow)
  {
    EnsureEditable();
    if (!_lines.Remove(line))
    {
      throw new InvalidOperationException("The line does not belong to this bill.");
    }

    Touch(utcNow);
  }

  public void ChangeHeader(Guid? customerId, string customerName, DateOnly serviceDate, string? notes, DateTime utcNow)
  {
    EnsureEditable();
    CustomerId = customerId;
    CustomerNameSnapshot = DomainValidation.Required(customerName, nameof(customerName), MaxCustomerNameLength, MinCustomerNameLength);
    ServiceDate = serviceDate;
    Notes = DomainValidation.Optional(notes, MaxNotesLength);
    Touch(utcNow);
  }

  /// <summary>The bill keeps going without its (cancelled) appointment; the event keeps the reason.</summary>
  public void DetachAppointment(DateTime utcNow)
  {
    EnsureEditable();
    AppointmentId = null;
    Touch(utcNow);
  }

  /// <summary>Recomputes the money projections from the lines; callers validate the discounts first.</summary>
  public void Recalculate()
  {
    Subtotal = _lines.Where(line => line.IsChargeable).Sum(line => line.GrossAmount);
    DiscountTotal = _lines.Where(line => line.Type == BillLineType.Discount).Sum(line => line.UnitPrice);
    TipTotal = _lines.Where(line => line.Type == BillLineType.Tip && line.TipPaymentMode == TipPaymentMode.WithBill).Sum(line => line.UnitPrice);
    DirectTipTotal = _lines.Where(line => line.Type == BillLineType.Tip && line.TipPaymentMode == TipPaymentMode.Direct).Sum(line => line.UnitPrice);
    Total = Subtotal - DiscountTotal + TipTotal;
  }

  public void Close(string series, int number, Guid? paymentMethodId, string? paymentMethodName, Guid closedByUserId, DateTime utcNow)
  {
    if (Status != BillStatus.Open)
    {
      throw new InvalidOperationException($"The bill is {Status}; only an open bill can be closed.");
    }

    Recalculate();
    if ((Total > 0) != (paymentMethodId is not null))
    {
      throw new InvalidOperationException("A bill with money to charge needs its payment method, and only then.");
    }

    Series = series;
    Number = DomainValidation.EnsureRange(number, 1, int.MaxValue, nameof(number));
    PaymentMethodId = paymentMethodId;
    PaymentMethodNameSnapshot = paymentMethodId is null ? null : DomainValidation.Required(paymentMethodName, nameof(paymentMethodName), PaymentMethod.MaxNameLength, 1);
    ChargedTotal = Total;
    ChargedPaymentMethodId = PaymentMethodId;
    ChargedPaymentMethodName = PaymentMethodNameSnapshot;
    ClosedByUserId = closedByUserId;
    ClosedAt = DomainValidation.EnsureUtc(utcNow, nameof(utcNow));
    Status = BillStatus.Closed;
    Touch(utcNow);
  }

  /// <summary>Only the approver changes the payment method of a closed bill (audited by the caller).</summary>
  public void ChangePaymentMethod(Guid? paymentMethodId, string? paymentMethodName, DateTime utcNow)
  {
    if (Status is not (BillStatus.Closed or BillStatus.Unapproved))
    {
      throw new InvalidOperationException("Only a closed bill has a payment method to change.");
    }

    PaymentMethodId = paymentMethodId;
    PaymentMethodNameSnapshot = paymentMethodId is null ? null : DomainValidation.Required(paymentMethodName, nameof(paymentMethodName), PaymentMethod.MaxNameLength, 1);
    Touch(utcNow);
  }

  public void Approve(Guid approvalId, Guid approvedByUserId, DateTime utcNow, bool selfApproved)
  {
    if (Status is not (BillStatus.Closed or BillStatus.Unapproved))
    {
      throw new InvalidOperationException($"The bill is {Status}; only a closed bill can be approved.");
    }

    CurrentApprovalId = approvalId;
    ApprovedByUserId = approvedByUserId;
    ApprovedAt = DomainValidation.EnsureUtc(utcNow, nameof(utcNow));
    IsSelfApproved = selfApproved;
    Status = BillStatus.Approved;
    Touch(utcNow);
  }

  /// <summary>
  /// ADR-0020 §3: the postings of the current approval were reversed; the bill waits for correction and a new
  /// approval (or is voided). Its approval keeps its own copy and its postings.
  /// </summary>
  public void Unapprove(DateTime utcNow)
  {
    if (Status != BillStatus.Approved)
    {
      throw new InvalidOperationException($"The bill is {Status}; only an approved bill can be unapproved.");
    }

    CurrentApprovalId = null;
    ApprovedByUserId = null;
    ApprovedAt = null;
    IsSelfApproved = false;
    Status = BillStatus.Unapproved;
    Touch(utcNow);
  }

  /// <summary>Final. Only a bill without postings (open, closed or unapproved) can be voided; it keeps its number.</summary>
  public void Void(string reason, Guid voidedByUserId, DateTime utcNow)
  {
    if (Status is not (BillStatus.Open or BillStatus.Closed or BillStatus.Unapproved))
    {
      throw new InvalidOperationException($"The bill is {Status} and cannot be voided.");
    }

    VoidReason = DomainValidation.Required(reason?.Trim(), nameof(reason), MaxReasonLength, MinReasonLength);
    VoidedByUserId = voidedByUserId;
    VoidedAt = DomainValidation.EnsureUtc(utcNow, nameof(utcNow));
    Status = BillStatus.Voided;
    Touch(utcNow);
  }

  /// <summary>Lines change while the bill is open, and (by the approver, audited) while it waits for approval.</summary>
  public void EnsureEditable()
  {
    if (Status is not (BillStatus.Open or BillStatus.Closed or BillStatus.Unapproved))
    {
      throw new InvalidOperationException($"The bill is {Status} and cannot change.");
    }
  }

  public void Touch(DateTime utcNow) => UpdatedAt = DomainValidation.EnsureUtc(utcNow, nameof(utcNow));
}

public enum BillStatus
{
  /// <summary>Being built; no number.</summary>
  Open = 1,

  /// <summary>Closed and charged, with its number; waiting in the approval tray.</summary>
  Closed = 2,

  /// <summary>Posted.</summary>
  Approved = 3,

  /// <summary>Postings reversed; waiting for correction and a new approval (stage 4).</summary>
  Unapproved = 4,

  /// <summary>Final; keeps its number if it had one.</summary>
  Voided = 5
}
