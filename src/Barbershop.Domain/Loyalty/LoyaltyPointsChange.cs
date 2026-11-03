using Barbershop.Domain.Common;

namespace Barbershop.Domain.Loyalty;

/// <summary>
/// Append-only audit of a change to the fixed points of a service or membership type. The points of a catalog
/// item only matter for future accruals: accruals already made will keep the points they were granted.
/// </summary>
public sealed class LoyaltyPointsChange
{
  private LoyaltyPointsChange()
  {
  }

  public LoyaltyPointsChange(
      LoyaltyPointsTarget targetType,
      Guid targetId,
      int previousPoints,
      int newPoints,
      Guid changedByUserId,
      DateTime changedAt)
  {
    if (!Enum.IsDefined(targetType))
    {
      throw new ArgumentOutOfRangeException(nameof(targetType), "Unknown loyalty points target.");
    }

    TargetType = targetType;
    TargetId = targetId;
    PreviousPoints = LoyaltySettings.EnsurePoints(previousPoints, nameof(previousPoints));
    NewPoints = LoyaltySettings.EnsurePoints(newPoints, nameof(newPoints));
    ChangedByUserId = changedByUserId;
    ChangedAt = DomainValidation.EnsureUtc(changedAt, nameof(changedAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public LoyaltyPointsTarget TargetType { get; private set; }
  public Guid TargetId { get; private set; }
  public int PreviousPoints { get; private set; }
  public int NewPoints { get; private set; }
  public Guid ChangedByUserId { get; private set; }
  public DateTime ChangedAt { get; private set; }
}

public enum LoyaltyPointsTarget
{
  Service = 1,
  MembershipPlan = 2
}
