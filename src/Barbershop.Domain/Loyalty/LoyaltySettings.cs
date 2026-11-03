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

  private LoyaltySettings()
  {
  }

  public LoyaltySettings(bool isEnabled, int pointsValidityDays, DateTime createdAt)
  {
    Id = SingletonId;
    IsEnabled = isEnabled;
    PointsValidityDays = EnsureValidityDays(pointsValidityDays);
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
  }

  public int Id { get; private set; }
  public bool IsEnabled { get; private set; }

  /// <summary>Calendar days a point stays valid from the day it is earned.</summary>
  public int PointsValidityDays { get; private set; }

  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }
  public Guid? UpdatedByUserId { get; private set; }

  /// <summary>Mapped to PostgreSQL's xmin system column; used as an optimistic concurrency token.</summary>
  public uint Version { get; private set; }

  /// <summary>Applies the new values and returns the audit entry, or null when nothing changes.</summary>
  public LoyaltySettingsChange? Update(bool isEnabled, int pointsValidityDays, Guid changedByUserId, DateTime changedAt)
  {
    var validityDays = EnsureValidityDays(pointsValidityDays);
    if (isEnabled == IsEnabled && validityDays == PointsValidityDays)
    {
      return null;
    }

    var change = new LoyaltySettingsChange(
        IsEnabled, isEnabled, PointsValidityDays, validityDays, changedByUserId, changedAt);

    IsEnabled = isEnabled;
    PointsValidityDays = validityDays;
    UpdatedAt = change.ChangedAt;
    UpdatedByUserId = changedByUserId;
    return change;
  }

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
      DateTime changedAt)
  {
    PreviousIsEnabled = previousIsEnabled;
    NewIsEnabled = newIsEnabled;
    PreviousPointsValidityDays = previousPointsValidityDays;
    NewPointsValidityDays = newPointsValidityDays;
    ChangedByUserId = changedByUserId;
    ChangedAt = DomainValidation.EnsureUtc(changedAt, nameof(changedAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public bool PreviousIsEnabled { get; private set; }
  public bool NewIsEnabled { get; private set; }
  public int PreviousPointsValidityDays { get; private set; }
  public int NewPointsValidityDays { get; private set; }
  public Guid ChangedByUserId { get; private set; }
  public DateTime ChangedAt { get; private set; }
}
