using System.Text.RegularExpressions;
using System.Net.Mail;

namespace Barbershop.Domain.Common;

internal static partial class DomainValidation
{
  public static string Required(string? value, string paramName, int maxLength, int minLength = 1)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);

    var trimmed = value.Trim();
    if (trimmed.Length < minLength || trimmed.Length > maxLength)
    {
      throw new ArgumentOutOfRangeException(paramName, $"{paramName} must be between {minLength} and {maxLength} characters.");
    }

    return trimmed;
  }

  public static string? Optional(string? value, int maxLength)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return null;
    }

    var trimmed = value.Trim();
    if (trimmed.Length > maxLength)
    {
      throw new ArgumentOutOfRangeException(nameof(value), $"The value must be {maxLength} characters or fewer.");
    }

    return trimmed;
  }

  public static DateTime EnsureUtc(DateTime value, string paramName)
  {
    if (value == default)
    {
      throw new ArgumentOutOfRangeException(paramName, $"{paramName} must be a valid timestamp.");
    }

    return value.Kind switch
    {
      DateTimeKind.Utc => value,
      DateTimeKind.Local => value.ToUniversalTime(),
      _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
  }

  public static int EnsureRange(int value, int min, int max, string paramName)
  {
    if (value < min || value > max)
    {
      throw new ArgumentOutOfRangeException(paramName, $"{paramName} must be between {min} and {max}.");
    }

    return value;
  }

  public static long EnsurePositive(long value, string paramName)
  {
    if (value <= 0)
    {
      throw new ArgumentOutOfRangeException(paramName, $"{paramName} must be greater than zero.");
    }

    return value;
  }

  public static decimal? OptionalNonNegative(decimal? value, string paramName)
  {
    if (value is null)
    {
      return null;
    }

    if (value < 0)
    {
      throw new ArgumentOutOfRangeException(paramName, $"{paramName} cannot be negative.");
    }

    return value;
  }

  public static Uri? OptionalAbsoluteUri(string? value, string paramName)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return null;
    }

    if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
    {
      throw new ArgumentException($"{paramName} must be a valid absolute URL.", paramName);
    }

    return uri;
  }

  public static string NormalizeKey(string value) => value.Trim().ToUpperInvariant();

  public static string NormalizeEmail(string value) => value.Trim().ToUpperInvariant();

  public static string RequiredEmail(string? value, string paramName)
  {
    var normalized = Required(value, paramName, 256, 5);

    try
    {
      _ = new MailAddress(normalized);
      return normalized;
    }
    catch (FormatException exception)
    {
      throw new ArgumentException($"{paramName} must be a valid email address.", paramName, exception);
    }
  }

  public static string? OptionalEmail(string? value, string paramName)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return null;
    }

    return RequiredEmail(value, paramName);
  }

  public static string? OptionalAbsoluteUriString(string? value, string paramName)
      => OptionalAbsoluteUri(value, paramName)?.ToString();

  public static string EnsureHexColor(string value, string paramName)
  {
    var normalized = Required(value, paramName, 7, 7);
    if (!HexColorRegex().IsMatch(normalized))
    {
      throw new ArgumentException($"{paramName} must be a valid hex color in the format #RRGGBB.", paramName);
    }

    return normalized.ToUpperInvariant();
  }

  [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled)]
  private static partial Regex HexColorRegex();
}