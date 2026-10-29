using Barbershop.Domain.Common;

namespace Barbershop.Domain.Finance;

/// <summary>
/// Money received, recorded by hand. A <see cref="IncomeKind.Service"/> entry is a service a professional
/// performed and is split with them by percentage; a <see cref="IncomeKind.MembershipSale"/> entry is the
/// sale of one or more memberships and belongs entirely to the business.
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

  /// <summary>Truncated business share; the remainder always goes to <see cref="ProfessionalAmount"/> so the two sum to <see cref="Amount"/> exactly.</summary>
  public int BusinessAmount => (int)((long)Amount * BusinessPercentageSnapshot / 100);

  public int ProfessionalAmount => Amount - BusinessAmount;

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
  MembershipSale = 2
}
