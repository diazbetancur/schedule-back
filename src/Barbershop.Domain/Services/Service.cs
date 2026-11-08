using Barbershop.Domain.Common;
using Barbershop.Domain.Loyalty;

namespace Barbershop.Domain.Services;

/// <summary>
/// Settlement rate: price, business share and loyalty points used to settle a performed service. Each rate belongs
/// to one general service (ADR-0016 D3); the class keeps its historical name and table so every id and relation
/// (income, memberships, rewards) stays untouched.
/// </summary>
public sealed class Service
{
  private Service()
  {
  }

  /// <param name="generalServiceId">
  /// The general service the rate belongs to. When omitted it takes the provisional default of ADR-0016 D4
  /// (<see cref="ServiceCatalogDefaults.CorteGeneralServiceId"/>); the admin API always sends it explicitly.
  /// </param>
  public Service(string name, int basePrice, DateTime createdAt, int businessPercentage = 0, Guid? generalServiceId = null)
  {
    GeneralServiceId = EnsureGeneralService(generalServiceId ?? ServiceCatalogDefaults.CorteGeneralServiceId);
    Name = DomainValidation.Required(name, nameof(name), 120, 2);
    BasePrice = DomainValidation.EnsureRange(basePrice, 0, int.MaxValue, nameof(basePrice));
    BusinessPercentage = DomainValidation.EnsureRange(businessPercentage, 0, 100, nameof(businessPercentage));
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
    IsActive = true;
    IsDeleted = false;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public string Name { get; private set; } = string.Empty;
  public Guid GeneralServiceId { get; private set; }
  public int BasePrice { get; private set; }
  public int BusinessPercentage { get; private set; }

  /// <summary>
  /// Fixed loyalty points a paid service will grant; 0 means it grants none. Membership consumptions, courtesies,
  /// free services and redemptions never grant points. Changing it never alters accruals already made.
  /// </summary>
  public int LoyaltyPoints { get; private set; }
  public bool IsActive { get; private set; }
  public bool IsDeleted { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }

  public void Update(string name, int basePrice, DateTime updatedAt, int businessPercentage = 0)
  {
    Name = DomainValidation.Required(name, nameof(name), 120, 2);
    BasePrice = DomainValidation.EnsureRange(basePrice, 0, int.MaxValue, nameof(basePrice));
    BusinessPercentage = DomainValidation.EnsureRange(businessPercentage, 0, 100, nameof(businessPercentage));
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  /// <summary>Moves the rate to another general service; nothing already settled with it changes.</summary>
  public void AssignGeneralService(Guid generalServiceId, DateTime updatedAt)
  {
    GeneralServiceId = EnsureGeneralService(generalServiceId);
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public void SetLoyaltyPoints(int loyaltyPoints, DateTime updatedAt)
  {
    LoyaltyPoints = LoyaltySettings.EnsurePoints(loyaltyPoints, nameof(loyaltyPoints));
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

  private static Guid EnsureGeneralService(Guid generalServiceId)
    => generalServiceId == Guid.Empty
        ? throw new ArgumentException("A rate needs a general service.", nameof(generalServiceId))
        : generalServiceId;
}
