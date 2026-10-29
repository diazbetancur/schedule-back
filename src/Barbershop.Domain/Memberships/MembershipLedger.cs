using Barbershop.Domain.Common;

namespace Barbershop.Domain.Memberships;

/// <summary>
/// One consumption operation registered by an admin: a service of a sold membership used on a real date.
/// Append-only: it is never edited or deleted; a mistake is corrected with a <see cref="MembershipConsumptionReversal"/>.
/// The units it took from each balance line are its <see cref="MembershipMovement"/> rows.
/// </summary>
public sealed class MembershipConsumption
{
  public const int MaxQuantity = 1000;

  private MembershipConsumption()
  {
  }

  public MembershipConsumption(
      Guid customerMembershipId,
      Guid serviceId,
      string serviceNameSnapshot,
      int quantity,
      DateOnly serviceDate,
      Guid? staffProfileId,
      Guid idempotencyKey,
      Guid registeredByUserId,
      DateTime registeredAt)
  {
    if (idempotencyKey == Guid.Empty)
    {
      throw new ArgumentException("A consumption needs an idempotency key.", nameof(idempotencyKey));
    }

    CustomerMembershipId = customerMembershipId;
    ServiceId = serviceId;
    ServiceNameSnapshot = DomainValidation.Required(serviceNameSnapshot, nameof(serviceNameSnapshot), 120, 1);
    Quantity = DomainValidation.EnsureRange(quantity, 1, MaxQuantity, nameof(quantity));
    ServiceDate = serviceDate;
    StaffProfileId = staffProfileId;
    IdempotencyKey = idempotencyKey;
    RegisteredByUserId = registeredByUserId;
    RegisteredAt = DomainValidation.EnsureUtc(registeredAt, nameof(registeredAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid CustomerMembershipId { get; private set; }
  public Guid ServiceId { get; private set; }
  public string ServiceNameSnapshot { get; private set; } = string.Empty;
  public int Quantity { get; private set; }

  /// <summary>Real (Bogotá) date the service was given; may be earlier than the registration.</summary>
  public DateOnly ServiceDate { get; private set; }

  public Guid? StaffProfileId { get; private set; }

  /// <summary>Sent by the client once per intended consumption, so a retried request is not counted twice.</summary>
  public Guid IdempotencyKey { get; private set; }

  public Guid RegisteredByUserId { get; private set; }
  public DateTime RegisteredAt { get; private set; }
}

/// <summary>
/// Units taken from (consumption) or given back to (reversal) one balance line. Append-only; the balance of a
/// line is its granted quantity minus its consumption movements plus its reversal movements.
/// </summary>
public sealed class MembershipMovement
{
  private MembershipMovement()
  {
  }

  public MembershipMovement(
      Guid customerMembershipId,
      Guid membershipAllowanceId,
      Guid membershipConsumptionId,
      MembershipMovementType type,
      int quantity,
      Guid performedByUserId,
      DateTime createdAt)
  {
    CustomerMembershipId = customerMembershipId;
    MembershipAllowanceId = membershipAllowanceId;
    MembershipConsumptionId = membershipConsumptionId;
    Type = type;
    Quantity = DomainValidation.EnsureRange(quantity, 1, MembershipConsumption.MaxQuantity, nameof(quantity));
    PerformedByUserId = performedByUserId;
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid CustomerMembershipId { get; private set; }
  public Guid MembershipAllowanceId { get; private set; }
  public Guid MembershipConsumptionId { get; private set; }
  public MembershipMovementType Type { get; private set; }
  public int Quantity { get; private set; }
  public Guid PerformedByUserId { get; private set; }
  public DateTime CreatedAt { get; private set; }
}

/// <summary>
/// Reverses a whole consumption registered by mistake. Append-only; a consumption has at most one reversal.
/// It gives the units back to the same lines but never changes the membership's status.
/// </summary>
public sealed class MembershipConsumptionReversal
{
  private MembershipConsumptionReversal()
  {
  }

  public MembershipConsumptionReversal(Guid membershipConsumptionId, string reason, Guid performedByUserId, DateTime performedAt)
  {
    MembershipConsumptionId = membershipConsumptionId;
    Reason = DomainValidation.Required(reason, nameof(reason), 500, 3);
    PerformedByUserId = performedByUserId;
    PerformedAt = DomainValidation.EnsureUtc(performedAt, nameof(performedAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid MembershipConsumptionId { get; private set; }
  public string Reason { get; private set; } = string.Empty;
  public Guid PerformedByUserId { get; private set; }
  public DateTime PerformedAt { get; private set; }
}

public enum MembershipMovementType
{
  Consumption = 1,
  Reversal = 2
}
