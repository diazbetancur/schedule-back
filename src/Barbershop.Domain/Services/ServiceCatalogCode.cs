using System.Security.Cryptography;

namespace Barbershop.Domain.Services;

/// <summary>
/// Immutable public code of a catalog entry, generated once and never derived from its name, so renaming an entry
/// does not break a link built on it (ADR-0016 T11). Lower-case letters and digits without look-alikes.
/// </summary>
public static class ServiceCatalogCode
{
  public const int MinLength = 4;
  public const int MaxLength = 16;
  public const int GeneratedLength = 8;
  private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";

  public static string Generate()
  {
    Span<char> code = stackalloc char[GeneratedLength];
    for (var index = 0; index < code.Length; index++)
    {
      code[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
    }

    return new string(code);
  }

  /// <summary>
  /// Comparison key of a catalog name: outer spaces removed and upper-cased (invariant). Stored next to the name so a
  /// unique index in PostgreSQL rejects equivalent names, not only the pre-check of the service.
  /// </summary>
  public static string NormalizeName(string name) => name.Trim().ToUpperInvariant();

  public static string Ensure(string code, string paramName)
  {
    if (string.IsNullOrWhiteSpace(code)
        || code.Length is < MinLength or > MaxLength
        || !code.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9')))
    {
      throw new ArgumentException($"A public code has {MinLength} to {MaxLength} lower-case letters or digits.", paramName);
    }

    return code;
  }
}
