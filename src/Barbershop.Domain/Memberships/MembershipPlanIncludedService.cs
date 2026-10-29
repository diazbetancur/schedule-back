using Barbershop.Domain.Common;

namespace Barbershop.Domain.Memberships;

/// <summary>A catalog <see cref="Services.Service"/> a plan covers, referenced by id (names and prices are not duplicated).</summary>
public sealed class MembershipPlanIncludedService
{
  private MembershipPlanIncludedService()
  {
  }

  public MembershipPlanIncludedService(Guid membershipPlanId, Guid serviceId, int? quantity)
  {
    MembershipPlanId = membershipPlanId;
    ServiceId = serviceId;
    SetQuantity(quantity);
  }

  public Guid MembershipPlanId { get; private set; }
  public Guid ServiceId { get; private set; }

  /// <summary>Units of this service for per-service plans; null for pool plans.</summary>
  public int? Quantity { get; private set; }

  public void SetQuantity(int? quantity)
  {
    Quantity = quantity is { } value ? DomainValidation.EnsureRange(value, 1, MembershipPlan.MaxQuantity, nameof(quantity)) : null;
  }
}
