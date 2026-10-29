using Barbershop.Domain.Common;

namespace Barbershop.Domain.Memberships;

/// <summary>
/// The complete hand-over of a physical gift, registered by an admin. Append-only: a mistaken delivery is
/// corrected with a <see cref="MembershipGiftDeliveryReversal"/>, after which the gift can be delivered again.
/// <see cref="DeliveryNumber"/> counts the deliveries of one gift (1, 2, ...) and is unique per gift, so two
/// simultaneous deliveries of the same gift can never both be stored.
/// </summary>
public sealed class MembershipGiftDelivery
{
  private MembershipGiftDelivery()
  {
  }

  public MembershipGiftDelivery(
      MembershipGift gift,
      int deliveryNumber,
      DateOnly deliveredOn,
      Guid idempotencyKey,
      Guid registeredByUserId,
      DateTime registeredAt)
  {
    if (gift.Kind != MembershipGiftKind.Physical)
    {
      throw new ArgumentException("Only physical gifts are delivered.", nameof(gift));
    }

    if (idempotencyKey == Guid.Empty)
    {
      throw new ArgumentException("A delivery needs an idempotency key.", nameof(idempotencyKey));
    }

    MembershipGiftId = gift.Id;
    CustomerMembershipId = gift.CustomerMembershipId;
    DeliveryNumber = DomainValidation.EnsureRange(deliveryNumber, 1, int.MaxValue, nameof(deliveryNumber));
    // Always the whole gift: there are no partial deliveries.
    Quantity = gift.Quantity;
    DeliveredOn = deliveredOn;
    IdempotencyKey = idempotencyKey;
    RegisteredByUserId = registeredByUserId;
    RegisteredAt = DomainValidation.EnsureUtc(registeredAt, nameof(registeredAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid MembershipGiftId { get; private set; }
  public Guid CustomerMembershipId { get; private set; }
  public int DeliveryNumber { get; private set; }
  public int Quantity { get; private set; }

  /// <summary>Real (Bogotá) date the gift was handed over; may be earlier than the registration.</summary>
  public DateOnly DeliveredOn { get; private set; }

  /// <summary>Sent by the client once per intended delivery, so a retried request is not stored twice.</summary>
  public Guid IdempotencyKey { get; private set; }

  public Guid RegisteredByUserId { get; private set; }
  public DateTime RegisteredAt { get; private set; }
}

/// <summary>Reverses a delivery registered by mistake. Append-only; a delivery has at most one reversal.</summary>
public sealed class MembershipGiftDeliveryReversal
{
  private MembershipGiftDeliveryReversal()
  {
  }

  public MembershipGiftDeliveryReversal(Guid membershipGiftDeliveryId, string reason, Guid performedByUserId, DateTime performedAt)
  {
    MembershipGiftDeliveryId = membershipGiftDeliveryId;
    Reason = DomainValidation.Required(reason, nameof(reason), 500, 3);
    PerformedByUserId = performedByUserId;
    PerformedAt = DomainValidation.EnsureUtc(performedAt, nameof(performedAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid MembershipGiftDeliveryId { get; private set; }
  public string Reason { get; private set; } = string.Empty;
  public Guid PerformedByUserId { get; private set; }
  public DateTime PerformedAt { get; private set; }
}
