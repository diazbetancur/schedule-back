using System.Text.RegularExpressions;

namespace Barbershop.Domain.Common;

/// <summary>
/// Links the API stores and publishes to anyone (BUG-047): banner links, the landing map and the professionals'
/// social networks. Only absolute http/https addresses are accepted: javascript:, data:, vbscript: or file: would be
/// handed to every client as is, and a relative value such as "/staff" was parsed as file:///staff on Linux. Banners
/// open their link in a new tab as an external page (landing content guide), so there is no relative-link exception.
/// </summary>
public static partial class WebUrls
{
  /// <summary>The column length of banner links and social networks.</summary>
  public const int MaxLength = 2048;

  /// <summary>The column length of the landing map link.</summary>
  public const int MapsMaxLength = 2000;

  /// <summary>An absolute http or https URL, written with its scheme, with a host and without credentials.</summary>
  public static bool IsHttpUrl(string? value) => TryParseHttpUrl(value, out _);

  /// <summary>
  /// A Google Maps link over https: maps.google.*, google.*/maps, maps.app.goo.gl or goo.gl/maps (share links and
  /// the embed links the landing content guide asks for).
  /// </summary>
  public static bool IsGoogleMapsUrl(string? value)
  {
    if (!TryParseHttpUrl(value, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
    {
      return false;
    }

    var host = uri.IdnHost.ToLowerInvariant();
    if (host == "maps.app.goo.gl")
    {
      return true;
    }

    if (host == "goo.gl")
    {
      return uri.AbsolutePath.StartsWith("/maps", StringComparison.OrdinalIgnoreCase);
    }

    var match = GoogleHostRegex().Match(host);
    if (!match.Success)
    {
      return false;
    }

    return match.Groups["maps"].Success || uri.AbsolutePath.StartsWith("/maps", StringComparison.OrdinalIgnoreCase);
  }

  private static bool TryParseHttpUrl(string? value, out Uri uri)
  {
    uri = null!;
    if (string.IsNullOrWhiteSpace(value))
    {
      return false;
    }

    var trimmed = value.Trim();
    if (!trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        && !trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
    {
      return false;
    }

    if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)
        || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
        || string.IsNullOrEmpty(parsed.Host)
        || !string.IsNullOrEmpty(parsed.UserInfo))
    {
      return false;
    }

    uri = parsed;
    return true;
  }

  [GeneratedRegex(@"^(?:www\.|(?<maps>maps\.))?google\.[a-z]{2,3}(?:\.[a-z]{2})?$", RegexOptions.CultureInvariant)]
  private static partial Regex GoogleHostRegex();
}
