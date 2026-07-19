namespace Barbershop.Domain.Common;

/// <summary>
/// The business operates in a single timezone (America/Bogota, UTC-5, no DST).
/// Centralizes the offset so it's defined once instead of duplicated per call site.
/// </summary>
public static class BogotaClock
{
  public static readonly TimeSpan UtcOffset = TimeSpan.FromHours(-5);

  /// <summary>Converts a Bogotá wall-clock date/time into the equivalent UTC instant.</summary>
  public static DateTime ToUtc(DateOnly date, TimeOnly localTime)
      => DateTime.SpecifyKind(date.ToDateTime(localTime) - UtcOffset, DateTimeKind.Utc);

  /// <summary>Converts a UTC instant into the equivalent Bogotá wall-clock time (for display only).</summary>
  public static DateTime ToLocal(DateTime utc)
      => DateTime.SpecifyKind(utc.Add(UtcOffset), DateTimeKind.Unspecified);
}
