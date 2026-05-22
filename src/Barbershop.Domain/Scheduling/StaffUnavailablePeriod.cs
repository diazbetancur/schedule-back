using Barbershop.Domain.Common;
using Barbershop.Domain.Staff;

namespace Barbershop.Domain.Scheduling;

public sealed class StaffUnavailablePeriod
{
  private StaffUnavailablePeriod()
  {
  }

  public StaffUnavailablePeriod(Guid staffProfileId, DateTime startsAt, DateTime endsAt, DateTime createdAt, string? reason = null)
  {
    StaffProfileId = staffProfileId;
    Update(startsAt, endsAt, reason);
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid StaffProfileId { get; private set; }
  public DateTime StartsAt { get; private set; }
  public DateTime EndsAt { get; private set; }
  public string? Reason { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public StaffProfile StaffProfile { get; private set; } = null!;

  public void Update(DateTime startsAt, DateTime endsAt, string? reason = null)
  {
    var normalizedStartsAt = DomainValidation.EnsureUtc(startsAt, nameof(startsAt));
    var normalizedEndsAt = DomainValidation.EnsureUtc(endsAt, nameof(endsAt));
    if (normalizedEndsAt <= normalizedStartsAt)
    {
      throw new ArgumentException("EndsAt must be after StartsAt.", nameof(endsAt));
    }

    StartsAt = normalizedStartsAt;
    EndsAt = normalizedEndsAt;
    Reason = DomainValidation.Optional(reason, 500);
  }

  public bool Overlaps(DateTime startsAt, DateTime endsAt)
  {
    var normalizedStartsAt = DomainValidation.EnsureUtc(startsAt, nameof(startsAt));
    var normalizedEndsAt = DomainValidation.EnsureUtc(endsAt, nameof(endsAt));
    return StartsAt < normalizedEndsAt && normalizedStartsAt < EndsAt;
  }
}