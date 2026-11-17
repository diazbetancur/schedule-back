using Barbershop.Domain.Common;

namespace Barbershop.Domain.Finance;

/// <summary>
/// Money received, recorded by hand or posted by an approved bill (ADR-0020). A <see cref="IncomeKind.Service"/> entry is a
/// service a professional performed and is split with them by percentage; a <see cref="IncomeKind.MembershipSale"/> entry
/// is the sale of one or more memberships and a <see cref="IncomeKind.ProductSale"/> the sale of products, both entirely
/// of the business; a <see cref="IncomeKind.Tip"/> is entirely of the professional.
/// </summary>
public sealed class IncomeEntry
{
  /// <summary>A membership sale is never split with a professional.</summary>
  public const int MembershipSaleBusinessPercentage = 100;

  private IncomeEntry()
  {
  }

  public IncomeEntry(
      Guid serviceId,
      string serviceNameSnapshot,
      int basePriceSnapshot,
      Guid staffProfileId,
      int amount,
      bool isPromo,
      DateOnly occurredOn,
      Guid createdByUserId,
      DateTime createdAt,
      int businessPercentageSnapshot = 0)
  {
    Kind = IncomeKind.Service;
    ServiceId = serviceId;
    ServiceNameSnapshot = DomainValidation.Required(serviceNameSnapshot, nameof(serviceNameSnapshot), 120, 1);
    BasePriceSnapshot = DomainValidation.EnsureRange(basePriceSnapshot, 0, int.MaxValue, nameof(basePriceSnapshot));
    BusinessPercentageSnapshot = DomainValidation.EnsureRange(businessPercentageSnapshot, 0, 100, nameof(businessPercentageSnapshot));
    StaffProfileId = staffProfileId;
    Amount = DomainValidation.EnsureRange(amount, 0, int.MaxValue, nameof(amount));
    IsPromo = isPromo;
    OccurredOn = occurredOn;
    CreatedByUserId = createdByUserId;
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
    IsDeleted = false;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();

  /// <summary>Set at creation and never changed.</summary>
  public IncomeKind Kind { get; private set; }

  /// <summary>Required for service income; null for membership sales.</summary>
  public Guid? ServiceId { get; private set; }

  /// <summary>Required for service income; null for membership sales.</summary>
  public string? ServiceNameSnapshot { get; private set; }
  public int BasePriceSnapshot { get; private set; }
  public int BusinessPercentageSnapshot { get; private set; }
  /// <summary>Required for service income; null for membership sales, which have no professional.</summary>
  public Guid? StaffProfileId { get; private set; }
  public int Amount { get; private set; }
  public bool IsPromo { get; private set; }
  public DateOnly OccurredOn { get; private set; }
  public Guid CreatedByUserId { get; private set; }
  public bool IsDeleted { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }

  /// <summary>
  /// ADR-0020 P7: the value before discounts of an income posted by a bill (service or product). The professional's
  /// commission is computed on it, so a discount is borne by the business. Null on every other income, whose commission
  /// is computed on <see cref="Amount"/> as before.
  /// </summary>
  public int? CommissionBaseAmount { get; private set; }

  /// <summary>The bill and the line that posted this income; such an income is managed from the bill only.</summary>
  public Guid? BillId { get; private set; }
  public Guid? BillLineId { get; private set; }

  // A product sale (ADR-0020): the product, the units and the average unit cost copied when it was posted.
  public Guid? ProductId { get; private set; }
  public string? ProductNameSnapshot { get; private set; }
  public int? Quantity { get; private set; }
  public decimal? UnitCostSnapshot { get; private set; }

  /// <summary>
  /// ADR-0020 Q8: this income is the counter-entry of another one (a bill unapproved): same snapshots, amount and
  /// commission base negated, dated the day of the unapproval. The original is never edited.
  /// </summary>
  public Guid? ReversesIncomeEntryId { get; private set; }

  public bool IsReversal => ReversesIncomeEntryId is not null;

  /// <summary>
  /// The professional's share: the commission computed on <see cref="CommissionBaseAmount"/> when there is one (P7),
  /// otherwise on <see cref="Amount"/>. The business keeps the rest, so the two always sum to <see cref="Amount"/> (the
  /// business share of a discounted service can be smaller than the commission, even negative: the business bears it).
  /// </summary>
  public int ProfessionalAmount
  {
    get
    {
      var commissionBase = CommissionBaseAmount ?? Amount;
      return commissionBase - BusinessShare(commissionBase, BusinessPercentageSnapshot);
    }
  }

  /// <summary>Truncated business share; for incomes without a commission base it is the share of <see cref="Amount"/>.</summary>
  public int BusinessAmount => Amount - ProfessionalAmount;

  /// <summary>
  /// The split rule of every service amount: the business share is truncated and the professional gets the rest.
  /// Also used for the commission the business covers on a loyalty reward (ADR-0015 R14).
  /// </summary>
  public static int BusinessShare(int amount, int businessPercentage) => (int)((long)amount * businessPercentage / 100);

  /// <summary>ADR-0020: a service of an approved bill. Amount is net of its discounts; the commission uses the full price.</summary>
  public static IncomeEntry CreateBillService(
      Guid serviceId, string serviceNameSnapshot, int basePriceSnapshot, int businessPercentageSnapshot, Guid staffProfileId,
      int amount, int commissionBaseAmount, DateOnly occurredOn, Guid billId, Guid billLineId, Guid createdByUserId, DateTime createdAt)
  {
    var entry = new IncomeEntry(
        serviceId, serviceNameSnapshot, basePriceSnapshot, staffProfileId, amount, isPromo: false, occurredOn, createdByUserId,
        createdAt, businessPercentageSnapshot);
    entry.CommissionBaseAmount = DomainValidation.EnsureRange(commissionBaseAmount, amount, int.MaxValue, nameof(commissionBaseAmount));
    entry.BillId = billId;
    entry.BillLineId = billLineId;
    return entry;
  }

  /// <summary>ADR-0020 Q11: a product sold on an approved bill, 100 % of the business, with its unit cost copied.</summary>
  public static IncomeEntry CreateProductSale(
      Guid productId, string productNameSnapshot, int quantity, int unitPrice, decimal? unitCostSnapshot, int amount,
      DateOnly occurredOn, Guid billId, Guid billLineId, Guid createdByUserId, DateTime createdAt)
  {
    var gross = checked(quantity * unitPrice);
    return new IncomeEntry
    {
      Kind = IncomeKind.ProductSale,
      ProductId = productId,
      ProductNameSnapshot = DomainValidation.Required(productNameSnapshot, nameof(productNameSnapshot), 120, 1),
      Quantity = DomainValidation.EnsureRange(quantity, 1, int.MaxValue, nameof(quantity)),
      BasePriceSnapshot = DomainValidation.EnsureRange(unitPrice, 0, int.MaxValue, nameof(unitPrice)),
      // Null when the product never had a receipt with cost: its profit is shown as "sin costo registrado".
      UnitCostSnapshot = unitCostSnapshot is null or >= 0 ? unitCostSnapshot : throw new ArgumentOutOfRangeException(nameof(unitCostSnapshot)),
      BusinessPercentageSnapshot = 100,
      Amount = DomainValidation.EnsureRange(amount, 0, gross, nameof(amount)),
      CommissionBaseAmount = gross,
      IsPromo = false,
      OccurredOn = occurredOn,
      BillId = billId,
      BillLineId = billLineId,
      CreatedByUserId = createdByUserId,
      CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt)),
      IsDeleted = false,
    };
  }

  /// <summary>ADR-0020 Q3: a tip charged with an approved bill, 100 % of the professional.</summary>
  public static IncomeEntry CreateTip(
      Guid staffProfileId, int amount, DateOnly occurredOn, Guid billId, Guid billLineId, Guid createdByUserId, DateTime createdAt)
      => new()
      {
        Kind = IncomeKind.Tip,
        StaffProfileId = staffProfileId,
        BasePriceSnapshot = 0,
        BusinessPercentageSnapshot = 0,
        Amount = DomainValidation.EnsureRange(amount, 1, int.MaxValue, nameof(amount)),
        IsPromo = false,
        OccurredOn = occurredOn,
        BillId = billId,
        BillLineId = billLineId,
        CreatedByUserId = createdByUserId,
        CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt)),
        IsDeleted = false,
      };

  /// <summary>
  /// ADR-0020 Q8: the counter-entry of an income posted by a bill. The commission is the exact mirror of the original
  /// (the business share is truncated toward zero on both signs), so the original plus its counter-entry add up to 0.
  /// </summary>
  public static IncomeEntry CreateReversal(IncomeEntry original, DateOnly occurredOn, Guid createdByUserId, DateTime createdAt)
  {
    if (original.BillLineId is null || original.IsReversal)
    {
      throw new InvalidOperationException("Only an income posted by a bill, and not a counter-entry, can be reversed.");
    }

    return new IncomeEntry
    {
      Kind = original.Kind,
      ServiceId = original.ServiceId,
      ServiceNameSnapshot = original.ServiceNameSnapshot,
      BasePriceSnapshot = original.BasePriceSnapshot,
      BusinessPercentageSnapshot = original.BusinessPercentageSnapshot,
      StaffProfileId = original.StaffProfileId,
      Amount = -original.Amount,
      CommissionBaseAmount = original.CommissionBaseAmount is { } commissionBase ? -commissionBase : null,
      IsPromo = false,
      OccurredOn = occurredOn,
      BillId = original.BillId,
      BillLineId = original.BillLineId,
      ProductId = original.ProductId,
      ProductNameSnapshot = original.ProductNameSnapshot,
      Quantity = original.Quantity,
      UnitCostSnapshot = original.UnitCostSnapshot,
      ReversesIncomeEntryId = original.Id,
      CreatedByUserId = createdByUserId,
      CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt)),
      IsDeleted = false,
    };
  }

  public static IncomeEntry CreateMembershipSale(int amount, DateOnly occurredOn, Guid createdByUserId, DateTime createdAt)
  {
    return new IncomeEntry
    {
      Kind = IncomeKind.MembershipSale,
      BasePriceSnapshot = 0,
      BusinessPercentageSnapshot = MembershipSaleBusinessPercentage,
      Amount = DomainValidation.EnsureRange(amount, 0, int.MaxValue, nameof(amount)),
      IsPromo = false,
      OccurredOn = occurredOn,
      CreatedByUserId = createdByUserId,
      CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt)),
      IsDeleted = false,
    };
  }

  public void UpdateMembershipSale(int amount, DateOnly occurredOn, DateTime updatedAt)
  {
    EnsureKind(IncomeKind.MembershipSale);
    Amount = DomainValidation.EnsureRange(amount, 0, int.MaxValue, nameof(amount));
    OccurredOn = occurredOn;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public void Update(
      Guid serviceId,
      string serviceNameSnapshot,
      int basePriceSnapshot,
      Guid staffProfileId,
      int amount,
      bool isPromo,
      DateOnly occurredOn,
      DateTime updatedAt,
      int businessPercentageSnapshot = 0)
  {
    EnsureKind(IncomeKind.Service);
    ServiceId = serviceId;
    ServiceNameSnapshot = DomainValidation.Required(serviceNameSnapshot, nameof(serviceNameSnapshot), 120, 1);
    BasePriceSnapshot = DomainValidation.EnsureRange(basePriceSnapshot, 0, int.MaxValue, nameof(basePriceSnapshot));
    BusinessPercentageSnapshot = DomainValidation.EnsureRange(businessPercentageSnapshot, 0, 100, nameof(businessPercentageSnapshot));
    StaffProfileId = staffProfileId;
    Amount = DomainValidation.EnsureRange(amount, 0, int.MaxValue, nameof(amount));
    IsPromo = isPromo;
    OccurredOn = occurredOn;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public void MarkDeleted(DateTime updatedAt)
  {
    IsDeleted = true;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  private void EnsureKind(IncomeKind expected)
  {
    if (Kind != expected)
    {
      throw new InvalidOperationException($"The income entry is a {Kind} entry and its kind cannot change.");
    }
  }
}

public enum IncomeKind
{
  /// <summary>A service performed by a professional; split with them by the service percentage.</summary>
  Service = 1,

  /// <summary>The sale of one or more memberships; 100% business, no professional.</summary>
  MembershipSale = 2,

  /// <summary>ADR-0020: products sold on an approved bill; 100% business (Q11).</summary>
  ProductSale = 3,

  /// <summary>ADR-0020: a tip charged with an approved bill; 100% of the professional (Q3).</summary>
  Tip = 4
}
