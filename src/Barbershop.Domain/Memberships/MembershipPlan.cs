using Barbershop.Domain.Common;

namespace Barbershop.Domain.Memberships;

/// <summary>
/// A membership type in the catalog. Sold memberships copy what they need from it at sale time,
/// so editing, deactivating or deleting a plan never changes memberships already sold.
/// </summary>
public sealed class MembershipPlan
{
  public const int MaxQuantity = 1000;
  public const int MaxDurationDays = 3650;

  private MembershipPlan()
  {
  }

  public MembershipPlan(
      string name,
      string? description,
      int price,
      MembershipPlanMode mode,
      int? poolQuantity,
      int defaultDurationDays,
      string terms,
      string? specificConditions,
      DateTime createdAt)
  {
    SetDetails(name, description, price, mode, poolQuantity, defaultDurationDays, terms, specificConditions);
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
    IsActive = true;
    IsDeleted = false;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public string Name { get; private set; } = string.Empty;
  public string? Description { get; private set; }
  public int Price { get; private set; }
  public MembershipPlanMode Mode { get; private set; }

  /// <summary>Shared quantity for <see cref="MembershipPlanMode.Pool"/>; null for per-service plans.</summary>
  public int? PoolQuantity { get; private set; }

  public int DefaultDurationDays { get; private set; }
  public string Terms { get; private set; } = string.Empty;
  public string? SpecificConditions { get; private set; }
  public bool IsActive { get; private set; }
  public bool IsDeleted { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }

  /// <summary>Mapped to PostgreSQL's xmin system column; used as an optimistic concurrency token.</summary>
  public uint Version { get; private set; }

  public ICollection<MembershipPlanIncludedService> Services { get; } = [];
  public ICollection<MembershipPlanGiftRule> GiftRules { get; } = [];

  public void Update(
      string name,
      string? description,
      int price,
      MembershipPlanMode mode,
      int? poolQuantity,
      int defaultDurationDays,
      string terms,
      string? specificConditions,
      DateTime updatedAt)
  {
    SetDetails(name, description, price, mode, poolQuantity, defaultDurationDays, terms, specificConditions);
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public void Activate(DateTime updatedAt)
  {
    IsActive = true;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public void Deactivate(DateTime updatedAt)
  {
    IsActive = false;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public void MarkDeleted(DateTime updatedAt)
  {
    IsDeleted = true;
    IsActive = false;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  private void SetDetails(
      string name,
      string? description,
      int price,
      MembershipPlanMode mode,
      int? poolQuantity,
      int defaultDurationDays,
      string terms,
      string? specificConditions)
  {
    if (!Enum.IsDefined(mode))
    {
      throw new ArgumentOutOfRangeException(nameof(mode), "Unknown membership plan mode.");
    }

    if (mode == MembershipPlanMode.Pool && poolQuantity is null)
    {
      throw new ArgumentException("A pool plan needs a pool quantity.", nameof(poolQuantity));
    }

    if (mode == MembershipPlanMode.PerService && poolQuantity is not null)
    {
      throw new ArgumentException("A per-service plan has no pool quantity.", nameof(poolQuantity));
    }

    Name = DomainValidation.Required(name, nameof(name), 120, 2);
    Description = DomainValidation.Optional(description, 1000);
    Price = DomainValidation.EnsureRange(price, 0, int.MaxValue, nameof(price));
    Mode = mode;
    PoolQuantity = poolQuantity is { } pool ? DomainValidation.EnsureRange(pool, 1, MaxQuantity, nameof(poolQuantity)) : null;
    DefaultDurationDays = DomainValidation.EnsureRange(defaultDurationDays, 1, MaxDurationDays, nameof(defaultDurationDays));
    Terms = DomainValidation.Required(terms, nameof(terms), 4000);
    SpecificConditions = DomainValidation.Optional(specificConditions, 2000);
  }
}

public enum MembershipPlanMode
{
  /// <summary>One shared quantity consumable by any of the plan's services.</summary>
  Pool = 1,

  /// <summary>An independent quantity for each service.</summary>
  PerService = 2
}
