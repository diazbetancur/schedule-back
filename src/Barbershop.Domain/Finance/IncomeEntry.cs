using Barbershop.Domain.Common;

namespace Barbershop.Domain.Finance;

public sealed class IncomeEntry
{
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
  public Guid ServiceId { get; private set; }
  public string ServiceNameSnapshot { get; private set; } = string.Empty;
  public int BasePriceSnapshot { get; private set; }
  public int BusinessPercentageSnapshot { get; private set; }
  public Guid StaffProfileId { get; private set; }
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
}
