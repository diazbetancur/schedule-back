using Barbershop.Domain.Common;

namespace Barbershop.Domain.Staff;

/// <summary>
/// A general service a professional offers and how long it takes them (ADR-0016 D6). Booking uses it: the customer
/// picks one of the professional's active offerings and the slot lasts its duration (D10). Changing it never moves
/// existing appointments, which keep the duration they reserved (D11).
/// </summary>
public sealed class StaffServiceOffering
{
  public const int MinDurationMinutes = 10;
  public const int MaxDurationMinutes = 240;

  private StaffServiceOffering()
  {
  }

  public StaffServiceOffering(Guid staffProfileId, Guid generalServiceId, int durationMinutes, bool isActive, DateTime createdAt)
  {
    StaffProfileId = staffProfileId == Guid.Empty
        ? throw new ArgumentException("An offering needs a professional.", nameof(staffProfileId))
        : staffProfileId;
    GeneralServiceId = generalServiceId == Guid.Empty
        ? throw new ArgumentException("An offering needs a general service.", nameof(generalServiceId))
        : generalServiceId;
    DurationMinutes = EnsureDuration(durationMinutes, nameof(durationMinutes));
    IsActive = isActive;
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid StaffProfileId { get; private set; }
  public Guid GeneralServiceId { get; private set; }
  public int DurationMinutes { get; private set; }
  public bool IsActive { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }

  /// <summary>Changes the duration and state; returns whether anything changed (UpdatedAt only moves then).</summary>
  public bool Update(int durationMinutes, bool isActive, DateTime updatedAt)
  {
    var duration = EnsureDuration(durationMinutes, nameof(durationMinutes));
    if (duration == DurationMinutes && isActive == IsActive)
    {
      return false;
    }

    DurationMinutes = duration;
    IsActive = isActive;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
    return true;
  }

  public static bool IsValidDuration(int durationMinutes)
      => durationMinutes is >= MinDurationMinutes and <= MaxDurationMinutes;

  public static int EnsureDuration(int durationMinutes, string paramName)
      => DomainValidation.EnsureRange(durationMinutes, MinDurationMinutes, MaxDurationMinutes, paramName);
}
