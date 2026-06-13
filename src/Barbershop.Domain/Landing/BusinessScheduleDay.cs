using Barbershop.Domain.Common;

namespace Barbershop.Domain.Landing;

public sealed class BusinessScheduleDay
{
  private BusinessScheduleDay()
  {
  }

  public BusinessScheduleDay(int dayOfWeek, bool isOpen, TimeOnly? openTime, TimeOnly? closeTime)
  {
    DayOfWeek = DomainValidation.EnsureRange(dayOfWeek, 0, 6, nameof(dayOfWeek));
    IsOpen = isOpen;
    OpenTime = openTime;
    CloseTime = closeTime;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();

  /// <summary>0 = Monday … 6 = Sunday</summary>
  public int DayOfWeek { get; private set; }

  public bool IsOpen { get; private set; }

  public TimeOnly? OpenTime { get; private set; }

  public TimeOnly? CloseTime { get; private set; }
}
