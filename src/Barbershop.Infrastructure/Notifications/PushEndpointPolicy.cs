namespace Barbershop.Infrastructure.Notifications;

/// <summary>
/// Which push endpoints the server may call (BUG-106, SSRF). A browser subscription always points to its vendor's
/// push service, so only those hosts are accepted: Google FCM (Chrome, Android, Edge on Android, Opera, Samsung),
/// Mozilla (Firefox), Microsoft WNS (Edge on Windows) and Apple (Safari, iPhone). Anything else (localhost, IP
/// literals, private or cloud-metadata addresses, other domains) is rejected when subscribing and is never contacted
/// when sending. Being a fixed list of public domains, DNS rebinding cannot turn one into an internal address.
/// </summary>
internal static class PushEndpointPolicy
{
  private static readonly string[] ExactHosts =
  [
    "fcm.googleapis.com",
    "web.push.apple.com",
  ];

  private static readonly string[] HostSuffixes =
  [
    ".push.services.mozilla.com",
    ".notify.windows.com",
  ];

  public static bool IsAllowed(string? endpoint)
  {
    if (string.IsNullOrWhiteSpace(endpoint)
        || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
        || uri.Scheme != Uri.UriSchemeHttps
        || !uri.IsDefaultPort
        || !string.IsNullOrEmpty(uri.UserInfo)
        || uri.HostNameType != UriHostNameType.Dns)
    {
      return false;
    }

    var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();

    return ExactHosts.Contains(host, StringComparer.Ordinal)
        || HostSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.Ordinal) && host.Length > suffix.Length);
  }
}
