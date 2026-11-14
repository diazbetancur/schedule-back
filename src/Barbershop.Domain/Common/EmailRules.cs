using System.Text.RegularExpressions;

namespace Barbershop.Domain.Common;

/// <summary>
/// The rule for an email typed into the system (BUG-004, BUG-017). System.Net.Mail.MailAddress accepted
/// "a b@c.com", "Nombre &lt;mail&gt;" (stored with the display name) and "usuario@localhost", and nothing stopped
/// invisible characters (U+200B, U+200C, U+202E) or look-alike letters (Cyrillic «а») that make an account look
/// identical to an existing one. A written email must be a plain address made only of ASCII letters, digits and the
/// usual symbols, with a domain that has a top-level part. Applied when an email is written (registration, customer
/// profiles, professionals, appointment contacts); existing accounts keep signing in with the email they have.
/// </summary>
public static partial class EmailRules
{
  public const int MaxLength = 256;
  public const int MinLength = 5;

  public const string InvalidMessage =
      "Escribe un correo válido, por ejemplo nombre@correo.com (sin espacios, tildes ni caracteres especiales; máximo 256 caracteres).";

  /// <summary>Whether <paramref name="value"/> (trimmed) is an acceptable address to store.</summary>
  public static bool IsValid(string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return false;
    }

    var email = value.Trim();
    if (email.Length is < MinLength or > MaxLength)
    {
      return false;
    }

    var at = email.IndexOf('@');
    if (at <= 0 || at != email.LastIndexOf('@') || at == email.Length - 1)
    {
      return false;
    }

    var local = email[..at];
    var domain = email[(at + 1)..];
    return local.Length <= 64 && LocalPartRegex().IsMatch(local) && DomainRegex().IsMatch(domain) && domain.Length <= 253;
  }

  /// <summary>The value stored for a valid email: trimmed, as typed (unicity uses the upper-cased form).</summary>
  public static string Normalize(string value) => value.Trim();

  // Dot-atom of RFC 5322 restricted to ASCII: no leading, trailing or doubled dots.
  [GeneratedRegex(@"^[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+(\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+)*$", RegexOptions.CultureInvariant)]
  private static partial Regex LocalPartRegex();

  // Labels of letters, digits and inner hyphens; the last one (top-level) has at least two letters.
  [GeneratedRegex(@"^([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,63}$", RegexOptions.CultureInvariant)]
  private static partial Regex DomainRegex();
}
