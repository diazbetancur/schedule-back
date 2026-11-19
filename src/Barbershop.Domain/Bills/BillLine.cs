using Barbershop.Domain.Common;

namespace Barbershop.Domain.Bills;

/// <summary>
/// One line of a bill. Prices are copied from the catalog when the line is added (whoever builds the bill never types
/// them) and frozen by the close: what was charged is what gets approved. <see cref="Amount"/> is what the line adds to
/// the total charged (negative for a discount, zero for consumptions, redemptions, courtesies and direct tips).
/// </summary>
public sealed class BillLine
{
  public const int MaxNameLength = 120;
  public const int MaxQuantity = 1_000;
  public const int MaxAmount = 100_000_000;

  private BillLine()
  {
  }

  private BillLine(BillLineType type, string name, int quantity, int unitPrice, DateTime createdAt)
  {
    Type = type;
    NameSnapshot = DomainValidation.Required(name, nameof(name), MaxNameLength, 1);
    Quantity = DomainValidation.EnsureRange(quantity, 1, MaxQuantity, nameof(quantity));
    UnitPrice = DomainValidation.EnsureRange(unitPrice, 0, MaxAmount, nameof(unitPrice));
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid BillId { get; private set; }
  public int Position { get; private set; }
  public BillLineType Type { get; private set; }

  /// <summary>Who performed the service or receives the tip; informative only on a product (Q11).</summary>
  public Guid? StaffProfileId { get; private set; }

  public Guid? GeneralServiceId { get; private set; }

  /// <summary>The rate (price list entry); a service line opened from an appointment has none until someone picks it.</summary>
  public Guid? ServiceId { get; private set; }

  public string NameSnapshot { get; private set; } = string.Empty;
  public int Quantity { get; private set; } = 1;

  /// <summary>
  /// Catalog price of the service or product, the amount of a discount or a tip, or (a service redemption) the base
  /// price copied for the commission the business covers (ADR-0015 R14).
  /// </summary>
  public int UnitPrice { get; private set; }

  /// <summary>Share of the business copied from the rate (services, membership consumptions and service redemptions).</summary>
  public int? BusinessPercentageSnapshot { get; private set; }

  /// <summary>Q10: a service given for free: no income, no commission, no points.</summary>
  public bool IsCourtesy { get; private set; }

  /// <summary>Reason of a courtesy or a discount (required for both).</summary>
  public string? Reason { get; private set; }

  public Guid? ProductId { get; private set; }
  public Guid? CustomerMembershipId { get; private set; }
  public Guid? LoyaltyRedemptionId { get; private set; }

  /// <summary>A discount on one line; null when the discount is on the whole bill.</summary>
  public Guid? AppliesToLineId { get; private set; }

  public TipPaymentMode? TipPaymentMode { get; private set; }

  /// <summary>What the line adds to the total charged.</summary>
  public int Amount { get; private set; }

  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }

  /// <summary>
  /// SONDA BUG-177 (ADR-0021 L9): a line removed while its bill was being corrected, kept because the reversed postings
  /// of the previous approval point to it. It no longer counts anywhere; a line without postings is deleted instead.
  /// </summary>
  public DateTime? RemovedAt { get; private set; }

  /// <summary>Services that are not courtesies and products: the lines discounts apply to.</summary>
  public bool IsChargeable => Type == BillLineType.Product || (Type == BillLineType.Service && !IsCourtesy);

  /// <summary>Value before discounts of a chargeable line.</summary>
  public int GrossAmount => IsChargeable ? checked(Quantity * UnitPrice) : 0;

  public static BillLine Service(
      Guid staffProfileId, Guid generalServiceId, string name, Guid? serviceId, int price, int? businessPercentage,
      bool isCourtesy, string? courtesyReason, DateTime createdAt)
  {
    var line = new BillLine(BillLineType.Service, name, 1, serviceId is null ? 0 : price, createdAt)
    {
      StaffProfileId = staffProfileId,
      GeneralServiceId = generalServiceId,
      ServiceId = serviceId,
      BusinessPercentageSnapshot = serviceId is null ? null : DomainValidation.EnsureRange(businessPercentage ?? -1, 0, 100, nameof(businessPercentage)),
    };
    line.SetCourtesy(isCourtesy, courtesyReason);
    return line;
  }

  public static BillLine Product(Guid productId, string name, int quantity, int unitPrice, Guid? staffProfileId, DateTime createdAt)
  {
    var line = new BillLine(BillLineType.Product, name, quantity, unitPrice, createdAt)
    {
      ProductId = productId,
      StaffProfileId = staffProfileId,
    };
    line.Refresh();
    return line;
  }

  public static BillLine MembershipConsumption(
      Guid staffProfileId, Guid generalServiceId, Guid serviceId, string name, int businessPercentage, Guid customerMembershipId,
      DateTime createdAt)
  {
    var line = new BillLine(BillLineType.MembershipConsumption, name, 1, 0, createdAt)
    {
      StaffProfileId = staffProfileId,
      GeneralServiceId = generalServiceId,
      ServiceId = serviceId,
      BusinessPercentageSnapshot = DomainValidation.EnsureRange(businessPercentage, 0, 100, nameof(businessPercentage)),
      CustomerMembershipId = customerMembershipId,
    };
    line.Refresh();
    return line;
  }

  /// <summary>
  /// A redemption already approved (its points are reserved). A service reward names the professional and the rate, and
  /// copies its base price and split for the commission the business covers; another reward has neither.
  /// </summary>
  public static BillLine Redemption(
      Guid loyaltyRedemptionId, string name, Guid? staffProfileId, Guid? generalServiceId, Guid? serviceId, int basePrice,
      int? businessPercentage, DateTime createdAt)
  {
    var line = new BillLine(BillLineType.Redemption, name, 1, serviceId is null ? 0 : basePrice, createdAt)
    {
      LoyaltyRedemptionId = loyaltyRedemptionId,
      StaffProfileId = staffProfileId,
      GeneralServiceId = generalServiceId,
      ServiceId = serviceId,
      BusinessPercentageSnapshot = serviceId is null ? null : DomainValidation.EnsureRange(businessPercentage ?? -1, 0, 100, nameof(businessPercentage)),
    };
    line.Refresh();
    return line;
  }

  public static BillLine Discount(int amount, string reason, Guid? appliesToLineId, DateTime createdAt)
  {
    var line = new BillLine(BillLineType.Discount, "Descuento", 1, DomainValidation.EnsureRange(amount, 1, MaxAmount, nameof(amount)), createdAt)
    {
      Reason = DomainValidation.Required(reason?.Trim(), nameof(reason), Bill.MaxReasonLength, Bill.MinReasonLength),
      AppliesToLineId = appliesToLineId,
    };
    line.Refresh();
    return line;
  }

  public static BillLine Tip(Guid staffProfileId, int amount, TipPaymentMode mode, DateTime createdAt)
  {
    var line = new BillLine(BillLineType.Tip, "Propina", 1, DomainValidation.EnsureRange(amount, 1, MaxAmount, nameof(amount)), createdAt)
    {
      StaffProfileId = staffProfileId,
      TipPaymentMode = mode,
    };
    line.Refresh();
    return line;
  }

  /// <summary>Picks or changes the rate of a service (or membership) line, copying its catalog values.</summary>
  public void SetRate(Guid generalServiceId, Guid serviceId, string name, int price, int businessPercentage, DateTime utcNow)
  {
    if (Type is not (BillLineType.Service or BillLineType.MembershipConsumption))
    {
      throw new InvalidOperationException("Only a service line has a rate.");
    }

    GeneralServiceId = generalServiceId;
    ServiceId = serviceId;
    NameSnapshot = DomainValidation.Required(name, nameof(name), MaxNameLength, 1);
    UnitPrice = Type == BillLineType.Service ? DomainValidation.EnsureRange(price, 0, MaxAmount, nameof(price)) : 0;
    BusinessPercentageSnapshot = DomainValidation.EnsureRange(businessPercentage, 0, 100, nameof(businessPercentage));
    Touch(utcNow);
  }

  public void SetStaff(Guid? staffProfileId, DateTime utcNow)
  {
    if (staffProfileId is null && Type is BillLineType.Service or BillLineType.MembershipConsumption or BillLineType.Tip)
    {
      throw new InvalidOperationException("This line needs its professional.");
    }

    StaffProfileId = staffProfileId;
    Touch(utcNow);
  }

  public void SetCourtesy(bool isCourtesy, string? reason, DateTime? utcNow = null)
  {
    if (Type != BillLineType.Service && isCourtesy)
    {
      throw new InvalidOperationException("Only a service can be a courtesy.");
    }

    IsCourtesy = isCourtesy;
    Reason = isCourtesy ? DomainValidation.Required(reason?.Trim(), nameof(reason), Bill.MaxReasonLength, Bill.MinReasonLength) : null;
    Refresh();
    if (utcNow is { } now)
    {
      Touch(now);
    }
  }

  public void SetQuantity(int quantity, DateTime utcNow)
  {
    if (Type != BillLineType.Product)
    {
      throw new InvalidOperationException("Only a product line has a quantity.");
    }

    Quantity = DomainValidation.EnsureRange(quantity, 1, MaxQuantity, nameof(quantity));
    Touch(utcNow);
  }

  public void SetAmount(int amount, string? reason, TipPaymentMode? tipMode, DateTime utcNow)
  {
    if (Type is not (BillLineType.Discount or BillLineType.Tip))
    {
      throw new InvalidOperationException("Only a discount or a tip has an amount.");
    }

    UnitPrice = DomainValidation.EnsureRange(amount, 1, MaxAmount, nameof(amount));
    if (Type == BillLineType.Discount)
    {
      Reason = DomainValidation.Required(reason?.Trim(), nameof(reason), Bill.MaxReasonLength, Bill.MinReasonLength);
    }
    else if (tipMode is { } mode)
    {
      TipPaymentMode = mode;
    }

    Touch(utcNow);
  }

  internal void MarkRemoved(DateTime utcNow)
  {
    RemovedAt = DomainValidation.EnsureUtc(utcNow, nameof(utcNow));
    UpdatedAt = RemovedAt;
  }

  /// <summary>SONDA BUG-182: quantity × unit price of a line fits the 32-bit amount columns.</summary>
  public static bool AmountFits(int quantity, int unitPrice) => (long)quantity * unitPrice <= int.MaxValue;

  internal void AttachTo(Guid billId, int position)
  {
    BillId = billId;
    Position = position;
  }

  private void Touch(DateTime utcNow)
  {
    Refresh();
    UpdatedAt = DomainValidation.EnsureUtc(utcNow, nameof(utcNow));
  }

  private void Refresh()
  {
    Amount = Type switch
    {
      BillLineType.Service => IsCourtesy ? 0 : UnitPrice,
      BillLineType.Product => checked(Quantity * UnitPrice),
      BillLineType.Discount => -UnitPrice,
      BillLineType.Tip => TipPaymentMode == Bills.TipPaymentMode.WithBill ? UnitPrice : 0,
      _ => 0
    };
  }
}

public enum BillLineType
{
  Service = 1,
  Product = 2,
  MembershipConsumption = 3,
  Redemption = 4,
  Discount = 5,
  Tip = 6
}

/// <summary>Q3: a tip charged with the bill (income of the professional) or paid straight to them (recorded only).</summary>
public enum TipPaymentMode
{
  WithBill = 1,
  Direct = 2
}
