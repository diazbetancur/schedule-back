using Barbershop.Domain.Common;

namespace Barbershop.Domain.Loyalty;

/// <summary>What earned the points of a lot. Lots created by merges keep the source of the original accrual.</summary>
public enum LoyaltySourceType
{
  ServiceRecord = 1,
  Membership = 2,
  Adjustment = 3
}

/// <summary>
/// Every change to a wallet. <see cref="LoyaltyMovement.Points"/> is always positive; the type gives the direction:
/// the opening types put points into a lot (or a debt), the others take them out. A lot's remaining points are the
/// sum of its movements with that sign, its opening counted once (<see cref="LoyaltyLot.InitialPoints"/> mirrors it).
/// </summary>
public enum LoyaltyMovementType
{
  /// <summary>Opens a lot: a service record approved as paid, or the first income link of a membership.</summary>
  Accrual = 1,

  /// <summary>Opens a lot in the surviving profile of a merge, with the remaining points of the source lot.</summary>
  MergeTransferIn = 2,

  /// <summary>Opens a lot: a manual credit.</summary>
  AdjustmentCredit = 3,

  /// <summary>Closes the remaining points of a lot after its last valid day.</summary>
  Expiration = 4,

  /// <summary>Takes the still usable points of a membership's lot when the membership is annulled.</summary>
  MembershipAnnulmentWithdrawal = 5,

  /// <summary>Takes points from a lot: a manual debit, never beyond the available balance.</summary>
  AdjustmentDebit = 6,

  /// <summary>Takes the remaining points of a source lot that a merge moved to the surviving profile.</summary>
  MergeTransferOut = 7,

  /// <summary>Takes points from a lot to settle a debt; it reduces both.</summary>
  DebtSettlement = 8,

  /// <summary>Takes the outstanding points of a source debt that a merge moved to the surviving profile.</summary>
  DebtMergeTransferOut = 9,

  /// <summary>Opens a debt in the surviving profile of a merge, with the outstanding points of the source debt.</summary>
  DebtMergeTransferIn = 10,

  /// <summary>Opens a debt of its own (reserved for reversing points already spent, ADR-0010; not created yet).</summary>
  DebtOpened = 11
}

/// <summary>Why a debt exists. Stage 4 creates none of its own: debts only move with merges and get settled.</summary>
public enum LoyaltyDebtOrigin
{
  /// <summary>Reserved for reversing points already spent (ADR-0010); not created yet.</summary>
  PointsReversal = 1,

  /// <summary>The outstanding part of a debt of a merged profile.</summary>
  MergeTransfer = 2
}

public enum LoyaltyAdjustmentDirection
{
  Credit = 1,
  Debit = 2
}

/// <summary>
/// Points earned together, with their own validity. <see cref="RemainingPoints"/> is a projection kept in the same
/// transaction as the movement that changes it, so it can always be reconciled with the movements.
/// </summary>
public sealed class LoyaltyLot
{
  private LoyaltyLot()
  {
  }

  private LoyaltyLot(
      Guid customerId,
      LoyaltySourceType sourceType,
      Guid sourceId,
      int points,
      DateTime accruedAt,
      DateOnly accruedOn,
      int validityDays,
      DateOnly expiresOn,
      Guid? originLotId,
      Guid? originCustomerId,
      DateTime createdAt)
  {
    CustomerId = customerId;
    SourceType = sourceType;
    SourceId = sourceId;
    InitialPoints = DomainValidation.EnsureRange(points, 1, int.MaxValue, nameof(points));
    RemainingPoints = InitialPoints;
    AccruedAt = DomainValidation.EnsureUtc(accruedAt, nameof(accruedAt));
    AccruedOn = accruedOn;
    ValidityDays = DomainValidation.EnsureRange(validityDays, LoyaltySettings.MinPointsValidityDays, LoyaltySettings.MaxPointsValidityDays, nameof(validityDays));
    ExpiresOn = expiresOn;
    OriginLotId = originLotId;
    OriginCustomerId = originCustomerId;
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
  }

  /// <summary>
  /// A new lot earned on <paramref name="accruedOn"/> (Bogotá). It stays usable for <paramref name="validityDays"/>
  /// calendar days counting that day, so its last valid day is accruedOn + validityDays − 1.
  /// </summary>
  public static LoyaltyLot Accrue(
      Guid customerId, LoyaltySourceType sourceType, Guid sourceId, int points, DateTime accruedAt, DateOnly accruedOn, int validityDays)
      => new(customerId, sourceType, sourceId, points, accruedAt, accruedOn, validityDays,
          accruedOn.AddDays(validityDays - 1), originLotId: null, originCustomerId: null, accruedAt);

  /// <summary>The lot a merge opens in the surviving profile: same source, dates and validity, never renewed.</summary>
  public static LoyaltyLot TransferredFrom(LoyaltyLot source, Guid targetCustomerId, int points, DateTime createdAt)
      => new(targetCustomerId, source.SourceType, source.SourceId, points, source.AccruedAt, source.AccruedOn, source.ValidityDays,
          source.ExpiresOn, source.Id, source.OriginCustomerId ?? source.CustomerId, createdAt);

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid CustomerId { get; private set; }
  public LoyaltySourceType SourceType { get; private set; }

  /// <summary>Service record, membership or adjustment that earned the points; lots created by merges keep it.</summary>
  public Guid SourceId { get; private set; }

  public int InitialPoints { get; private set; }
  public int RemainingPoints { get; private set; }
  public DateTime AccruedAt { get; private set; }

  /// <summary>Bogotá date of the original accrual.</summary>
  public DateOnly AccruedOn { get; private set; }

  /// <summary>The validity applied when the points were earned; later changes to the settings do not alter it.</summary>
  public int ValidityDays { get; private set; }

  /// <summary>Last valid day, inclusive (Bogotá date).</summary>
  public DateOnly ExpiresOn { get; private set; }

  /// <summary>For a lot created by a merge: the lot it came from (null for the original accrual).</summary>
  public Guid? OriginLotId { get; private set; }

  /// <summary>For a lot created by a merge: the profile that originally earned the points.</summary>
  public Guid? OriginCustomerId { get; private set; }

  public DateTime CreatedAt { get; private set; }

  /// <summary>Mapped to PostgreSQL's xmin system column; used as an optimistic concurrency token.</summary>
  public uint Version { get; private set; }

  public bool IsExpiredOn(DateOnly today) => today > ExpiresOn;

  public int UsablePointsOn(DateOnly today) => IsExpiredOn(today) ? 0 : RemainingPoints;

  /// <summary>Takes points out of the lot; never more than it has left.</summary>
  public void Take(int points)
  {
    DomainValidation.EnsureRange(points, 1, RemainingPoints, nameof(points));
    RemainingPoints -= points;
  }
}

/// <summary>
/// Points owed by a profile. <see cref="OutstandingPoints"/> is a projection: the amount minus what settlements and
/// merges took, kept in the same transaction as those movements.
/// </summary>
public sealed class LoyaltyDebt
{
  private LoyaltyDebt()
  {
  }

  public LoyaltyDebt(Guid customerId, LoyaltyDebtOrigin origin, int points, DateTime createdAt, Guid? originDebtId = null, Guid? originCustomerId = null)
  {
    CustomerId = customerId;
    Origin = origin;
    Amount = DomainValidation.EnsureRange(points, 1, int.MaxValue, nameof(points));
    OutstandingPoints = Amount;
    OriginDebtId = originDebtId;
    OriginCustomerId = originCustomerId;
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
    IncurredAt = CreatedAt;
  }

  /// <summary>
  /// The debt a merge opens in the surviving profile for the outstanding part of <paramref name="source"/>. It keeps
  /// the source's <see cref="IncurredAt"/>, so a merge (or several) never makes an old debt look new.
  /// </summary>
  public static LoyaltyDebt TransferredFrom(LoyaltyDebt source, Guid targetCustomerId, int points, DateTime transferredAt)
      => new(targetCustomerId, LoyaltyDebtOrigin.MergeTransfer, points, transferredAt, source.Id, source.OriginCustomerId ?? source.CustomerId)
      {
        IncurredAt = source.IncurredAt
      };

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid CustomerId { get; private set; }
  public LoyaltyDebtOrigin Origin { get; private set; }
  public int Amount { get; private set; }
  public int OutstandingPoints { get; private set; }

  /// <summary>For a debt moved by a merge: the source debt and the profile that originally owed it.</summary>
  public Guid? OriginDebtId { get; private set; }

  public Guid? OriginCustomerId { get; private set; }

  /// <summary>When this row was written (for a debt moved by a merge: the merge).</summary>
  public DateTime CreatedAt { get; private set; }

  /// <summary>
  /// When the original debt arose; kept through every merge. Settlement order (oldest first) uses this, never
  /// <see cref="CreatedAt"/>.
  /// </summary>
  public DateTime IncurredAt { get; private set; }

  /// <summary>Mapped to PostgreSQL's xmin system column; used as an optimistic concurrency token.</summary>
  public uint Version { get; private set; }

  public void Reduce(int points)
  {
    DomainValidation.EnsureRange(points, 1, OutstandingPoints, nameof(points));
    OutstandingPoints -= points;
  }
}

/// <summary>A manual credit or debit with its reason. The idempotency key makes a retried request return it.</summary>
public sealed class LoyaltyAdjustment
{
  public const int MinReasonLength = 3;
  public const int MaxReasonLength = 500;

  private LoyaltyAdjustment()
  {
  }

  public LoyaltyAdjustment(
      Guid customerId, LoyaltyAdjustmentDirection direction, int points, string reason, Guid idempotencyKey, Guid performedByUserId, DateTime performedAt)
  {
    if (idempotencyKey == Guid.Empty)
    {
      throw new ArgumentException("The idempotency key is required.", nameof(idempotencyKey));
    }

    CustomerId = customerId;
    Direction = direction;
    Points = DomainValidation.EnsureRange(points, 1, LoyaltySettings.MaxPointsPerItem, nameof(points));
    Reason = DomainValidation.Required(reason, nameof(reason), MaxReasonLength, MinReasonLength);
    IdempotencyKey = idempotencyKey;
    PerformedByUserId = performedByUserId;
    PerformedAt = DomainValidation.EnsureUtc(performedAt, nameof(performedAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid CustomerId { get; private set; }
  public LoyaltyAdjustmentDirection Direction { get; private set; }
  public int Points { get; private set; }
  public string Reason { get; private set; } = string.Empty;
  public Guid IdempotencyKey { get; private set; }
  public Guid PerformedByUserId { get; private set; }
  public DateTime PerformedAt { get; private set; }
}

/// <summary>Append-only history of a wallet: never edited or deleted; corrections are new movements.</summary>
public sealed class LoyaltyMovement
{
  private LoyaltyMovement()
  {
  }

  public LoyaltyMovement(
      Guid customerId,
      LoyaltyMovementType type,
      int points,
      DateTime performedAt,
      Guid? lotId = null,
      Guid? debtId = null,
      Guid? performedByUserId = null,
      Guid? adjustmentId = null,
      Guid? relatedCustomerId = null,
      string? reason = null)
  {
    if (lotId is null && debtId is null)
    {
      throw new ArgumentException("A movement belongs to a lot, a debt or both.");
    }

    CustomerId = customerId;
    Type = type;
    Points = DomainValidation.EnsureRange(points, 1, int.MaxValue, nameof(points));
    LotId = lotId;
    DebtId = debtId;
    PerformedByUserId = performedByUserId;
    AdjustmentId = adjustmentId;
    RelatedCustomerId = relatedCustomerId;
    Reason = reason;
    PerformedAt = DomainValidation.EnsureUtc(performedAt, nameof(performedAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid CustomerId { get; private set; }
  public LoyaltyMovementType Type { get; private set; }
  public int Points { get; private set; }
  public Guid? LotId { get; private set; }
  public Guid? DebtId { get; private set; }

  /// <summary>Null for system movements (expirations).</summary>
  public Guid? PerformedByUserId { get; private set; }

  public Guid? AdjustmentId { get; private set; }

  /// <summary>For merge movements: the other profile of the merge.</summary>
  public Guid? RelatedCustomerId { get; private set; }

  public string? Reason { get; private set; }
  public DateTime PerformedAt { get; private set; }

  /// <summary>+1 when the movement puts points into its lot, −1 when it takes them out (0 for debt-only movements).</summary>
  public static int LotSign(LoyaltyMovementType type) => type switch
  {
    LoyaltyMovementType.Accrual or LoyaltyMovementType.MergeTransferIn or LoyaltyMovementType.AdjustmentCredit => 1,
    LoyaltyMovementType.Expiration or LoyaltyMovementType.MembershipAnnulmentWithdrawal or LoyaltyMovementType.AdjustmentDebit
        or LoyaltyMovementType.MergeTransferOut or LoyaltyMovementType.DebtSettlement => -1,
    _ => 0
  };

  /// <summary>+1 when the movement opens its debt, −1 when it reduces it (0 for lot-only movements).</summary>
  public static int DebtSign(LoyaltyMovementType type) => type switch
  {
    LoyaltyMovementType.DebtMergeTransferIn or LoyaltyMovementType.DebtOpened => 1,
    LoyaltyMovementType.DebtSettlement or LoyaltyMovementType.DebtMergeTransferOut => -1,
    _ => 0
  };
}
