using Barbershop.Domain.Common;
using Barbershop.Domain.Staff;

namespace Barbershop.Domain.Scheduling;

public sealed class StaffAvailabilityRule
{
  private StaffAvailabilityRule()
  {
  }

  public StaffAvailabilityRule(Guid staffProfileId, DayOfWeek dayOfWeek, TimeOnly startTime, TimeOnly endTime, bool isActive = true)
  {
    if (endTime <= startTime)
    {
      throw new ArgumentException("End time must be after start time.", nameof(endTime));
    }

    StaffProfileId = staffProfileId;
    DayOfWeek = dayOfWeek;
    StartTime = startTime;
    EndTime = endTime;
    IsActive = isActive;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid StaffProfileId { get; private set; }
  public DayOfWeek DayOfWeek { get; private set; }
  public TimeOnly StartTime { get; private set; }
  public TimeOnly EndTime { get; private set; }
  public bool IsActive { get; private set; }
  public StaffProfile StaffProfile { get; private set; } = null!;

  public bool OverlapsWith(StaffAvailabilityRule other)
      => StaffProfileId == other.StaffProfileId
         && DayOfWeek == other.DayOfWeek
         && StartTime < other.EndTime
         && other.StartTime < EndTime;
}