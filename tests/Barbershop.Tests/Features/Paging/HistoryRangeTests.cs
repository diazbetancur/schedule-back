using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Common.Paging;

namespace Barbershop.Tests.Features.Paging;

/// <summary>The 30-day window of the histories (ADR-0016 B7), enforced on the server.</summary>
public sealed class HistoryRangeTests
{
  private static readonly DateOnly Today = new(2026, 11, 7);

  [Fact]
  public void WithoutDates_AHistoryShowsTheLastThirtyDaysEndingToday()
  {
    var range = HistoryRange.Resolve(null, null, Today);

    Assert.Equal((new DateOnly(2026, 10, 9), Today, 30), (range.From, range.To, range.Days));
  }

  [Fact]
  public void WithoutDates_AQueueHasNoWindow()
  {
    Assert.Null(HistoryRange.ResolveOptional(null, null));
  }

  [Fact]
  public void ThirtyCalendarDays_BothEndsIncluded_AreAccepted_AtAnyPeriod()
  {
    var recent = HistoryRange.Resolve(new DateOnly(2026, 10, 9), Today, Today);
    var old = HistoryRange.Resolve(new DateOnly(2024, 2, 1), new DateOnly(2024, 3, 1), Today);
    var single = HistoryRange.Resolve(Today, Today, Today);

    Assert.Equal(30, recent.Days);
    Assert.Equal(30, old.Days);
    Assert.Equal(1, single.Days);
  }

  [Theory]
  [InlineData("2026-10-08", "2026-11-07")]
  [InlineData("2024-02-01", "2024-03-02")]
  public void ThirtyOneDays_AreRejected_ForHistoriesAndQueues(string from, string to)
  {
    var (start, end) = (DateOnly.Parse(from), DateOnly.Parse(to));

    var history = Assert.Throws<ValidationProblemException>(() => HistoryRange.Resolve(start, end, Today));
    var queue = Assert.Throws<ValidationProblemException>(() => HistoryRange.ResolveOptional(start, end, "soldFrom", "soldTo"));

    Assert.Contains("to", history.Errors.Keys);
    Assert.Contains("soldTo", queue.Errors.Keys);
  }

  [Fact]
  public void WithOneEnd_TheWindowExtendsThirtyDaysFromIt()
  {
    var forward = HistoryRange.Resolve(new DateOnly(2025, 1, 1), null, Today);
    var backward = HistoryRange.Resolve(null, new DateOnly(2025, 1, 30), Today);

    Assert.Equal((new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 30)), (forward.From, forward.To));
    Assert.Equal((new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 30)), (backward.From, backward.To));
  }

  [Fact]
  public void AReversedRange_IsRejectedOnTheStart()
  {
    var error = Assert.Throws<ValidationProblemException>(() => HistoryRange.Resolve(Today, Today.AddDays(-1), Today));

    Assert.Contains("from", error.Errors.Keys);
  }
}
