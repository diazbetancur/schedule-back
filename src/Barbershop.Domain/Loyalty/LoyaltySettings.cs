using Barbershop.Domain.Common;

namespace Barbershop.Domain.Loyalty;

/// <summary>
/// The program-wide loyalty configuration: a single row (<see cref="SingletonId"/>) created by the migration.
/// The validity is global, not per service or membership type. Changing it only affects points earned
/// afterwards: each accrual will keep its own copy of the validity applied and its expiry date.
/// </summary>
public sealed class LoyaltySettings
{
  public const int SingletonId = 1;
  public const int MinPointsValidityDays = 1;
  public const int MaxPointsValidityDays = 3650;
  public const int DefaultPointsValidityDays = 365;

  /// <summary>Upper bound for the fixed points of a service or membership type.</summary>
  public const int MaxPointsPerItem = 100_000;

  /// <summary>Bounds and default (ADR-0015 R7) of the calendar days to approve and to deliver a redemption.</summary>
  public const int MinRedemptionDays = 1;
  public const int MaxRedemptionDays = 365;
  public const int DefaultRedemptionDays = 30;

  private LoyaltySettings()
  {
  }

  public LoyaltySettings(
      bool isEnabled,
      int pointsValidityDays,
      DateTime createdAt,
      int redemptionApprovalDays = DefaultRedemptionDays,
      int redemptionDeliveryDays = DefaultRedemptionDays)
  {
    Id = SingletonId;
    IsEnabled = isEnabled;
    PointsValidityDays = EnsureValidityDays(pointsValidityDays);
    RedemptionApprovalDays = EnsureRedemptionDays(redemptionApprovalDays, nameof(redemptionApprovalDays));
    RedemptionDeliveryDays = EnsureRedemptionDays(redemptionDeliveryDays, nameof(redemptionDeliveryDays));
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
  }

  public int Id { get; private set; }
  public bool IsEnabled { get; private set; }

  /// <summary>Calendar days a point stays valid from the day it is earned.</summary>
  public int PointsValidityDays { get; private set; }

  /// <summary>Calendar days to approve a redemption, counting the request day (Bogotá). Copied into each request.</summary>
  public int RedemptionApprovalDays { get; private set; } = DefaultRedemptionDays;

  /// <summary>Calendar days to deliver an approved redemption, counting the approval day (Bogotá). Copied into each request.</summary>
  public int RedemptionDeliveryDays { get; private set; } = DefaultRedemptionDays;

  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }
  public Guid? UpdatedByUserId { get; private set; }

  /// <summary>Mapped to PostgreSQL's xmin system column; used as an optimistic concurrency token.</summary>
  public uint Version { get; private set; }

  /// <summary>
  /// Applies the new values and returns the audit entry, or null when nothing changes. A null redemption deadline
  /// keeps the current one (older clients do not send it).
  /// </summary>
  public LoyaltySettingsChange? Update(
      bool isEnabled,
      int pointsValidityDays,
      Guid changedByUserId,
      DateTime changedAt,
      int? redemptionApprovalDays = null,
      int? redemptionDeliveryDays = null)
  {
    var validityDays = EnsureValidityDays(pointsValidityDays);
    var approvalDays = EnsureRedemptionDays(redemptionApprovalDays ?? RedemptionApprovalDays, nameof(redemptionApprovalDays));
    var deliveryDays = EnsureRedemptionDays(redemptionDeliveryDays ?? RedemptionDeliveryDays, nameof(redemptionDeliveryDays));
    if (isEnabled == IsEnabled && validityDays == PointsValidityDays
        && approvalDays == RedemptionApprovalDays && deliveryDays == RedemptionDeliveryDays)
    {
      return null;
    }

    var change = new LoyaltySettingsChange(
        IsEnabled, isEnabled, PointsValidityDays, validityDays, changedByUserId, changedAt,
        RedemptionApprovalDays, approvalDays, RedemptionDeliveryDays, deliveryDays);

    IsEnabled = isEnabled;
    PointsValidityDays = validityDays;
    RedemptionApprovalDays = approvalDays;
    RedemptionDeliveryDays = deliveryDays;
    UpdatedAt = change.ChangedAt;
    UpdatedByUserId = changedByUserId;
    return change;
  }

  public static int EnsureRedemptionDays(int days, string paramName)
      => DomainValidation.EnsureRange(days, MinRedemptionDays, MaxRedemptionDays, paramName);

  public static int EnsurePoints(int points, string paramName)
      => DomainValidation.EnsureRange(points, 0, MaxPointsPerItem, paramName);

  private static int EnsureValidityDays(int days)
      => DomainValidation.EnsureRange(days, MinPointsValidityDays, MaxPointsValidityDays, nameof(PointsValidityDays));
}

/// <summary>Append-only audit of a change to <see cref="LoyaltySettings"/>: it is never edited or deleted.</summary>
public sealed class LoyaltySettingsChange
{
  private LoyaltySettingsChange()
  {
  }

  public LoyaltySettingsChange(
      bool previousIsEnabled,
      bool newIsEnabled,
      int previousPointsValidityDays,
      int newPointsValidityDays,
      Guid changedByUserId,
      DateTime changedAt,
      int? previousRedemptionApprovalDays = null,
      int? newRedemptionApprovalDays = null,
      int? previousRedemptionDeliveryDays = null,
      int? newRedemptionDeliveryDays = null)
  {
    PreviousIsEnabled = previousIsEnabled;
    NewIsEnabled = newIsEnabled;
    PreviousPointsValidityDays = previousPointsValidityDays;
    NewPointsValidityDays = newPointsValidityDays;
    PreviousRedemptionApprovalDays = previousRedemptionApprovalDays;
    NewRedemptionApprovalDays = newRedemptionApprovalDays;
    PreviousRedemptionDeliveryDays = previousRedemptionDeliveryDays;
    NewRedemptionDeliveryDays = newRedemptionDeliveryDays;
    ChangedByUserId = changedByUserId;
    ChangedAt = DomainValidation.EnsureUtc(changedAt, nameof(changedAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public bool PreviousIsEnabled { get; private set; }
  public bool NewIsEnabled { get; private set; }
  public int PreviousPointsValidityDays { get; private set; }
  public int NewPointsValidityDays { get; private set; }

  /// <summary>Null in changes recorded before the redemption deadlines existed (ADR-0015).</summary>
  public int? PreviousRedemptionApprovalDays { get; private set; }

  public int? NewRedemptionApprovalDays { get; private set; }
  public int? PreviousRedemptionDeliveryDays { get; private set; }
  public int? NewRedemptionDeliveryDays { get; private set; }
  public Guid ChangedByUserId { get; private set; }
  public DateTime ChangedAt { get; private set; }
}
