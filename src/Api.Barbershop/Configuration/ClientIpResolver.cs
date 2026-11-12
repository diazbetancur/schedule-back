using System.Net;
using Microsoft.Extensions.Primitives;

namespace Api.Barbershop.Configuration;

/// <summary>
/// Picks the client address used as the rate-limit partition key (BUG-001).
/// </summary>
/// <remarks>
/// <para>
/// Render puts Cloudflare in front of every web service and documents <c>X-Forwarded-For</c> as the way to read the
/// client IP. A request therefore arrives as <c>client → Cloudflare edge → Render proxy → app</c>, and each hop
/// appends the address it received the request from. Entries to the left of the client are whatever the client
/// itself sent, so they can be forged.
/// </para>
/// <para>
/// <c>UseForwardedHeaders</c> (Program.cs) trusts the transport peer (the platform proxy) and consumes the rightmost
/// entry, which leaves the Cloudflare edge in <see cref="ConnectionInfo.RemoteIpAddress"/>. Keying by that address
/// spread one client over many edges and made the limit ineffective. This resolver walks the remaining chain from
/// right to left, skipping only addresses of trusted proxy networks (Cloudflare's published ranges and private or
/// loopback networks, at most <see cref="MaxTrustedHops"/> of them), and returns the first address that is not a
/// proxy: the client as seen by Cloudflare. Forged entries further left are never reached.
/// </para>
/// <para>
/// Assumption (not verifiable from the code): the app is only reachable through the platform proxy, the same
/// assumption the existing forwarded-headers configuration already makes. Without forwarding headers (local
/// development, tests) the key is the connection's own address.
/// </para>
/// </remarks>
public static class ClientIpResolver
{
    /// <summary>
    /// Proxy hops skipped at most. Cloudflare edge plus internal hops; a longer chain of "trusted" addresses is
    /// treated as forged and the next entry is used as is.
    /// </summary>
    public const int MaxTrustedHops = 3;

    private const int MaxKeyLength = 64;

    // Cloudflare's published ranges (https://www.cloudflare.com/ips-v4 and /ips-v6, read 2026-11-09).
    private static readonly string[] CloudflareNetworks =
    [
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18",
        "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17",
        "162.158.0.0/15", "104.16.0.0/13", "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
        "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2405:b500::/32", "2405:8100::/32",
        "2a06:98c0::/29", "2c0f:f248::/32",
    ];

    // Loopback, private, shared (CGNAT) and link-local networks: platform-internal hops, never a public client.
    private static readonly string[] InternalNetworks =
    [
        "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10", "169.254.0.0/16",
        "::1/128", "fc00::/7", "fe80::/10",
    ];

    private static readonly IPNetwork[] TrustedNetworks = CloudflareNetworks
        .Concat(InternalNetworks)
        .Select(IPNetwork.Parse)
        .ToArray();

    public static string Resolve(HttpContext httpContext)
        => Resolve(httpContext.Connection.RemoteIpAddress, httpContext.Request.Headers["X-Forwarded-For"]);

    /// <param name="remoteIpAddress">The connection address after <c>UseForwardedHeaders</c> ran.</param>
    /// <param name="forwardedFor">What is left of <c>X-Forwarded-For</c> after <c>UseForwardedHeaders</c> ran.</param>
    public static string Resolve(IPAddress? remoteIpAddress, StringValues forwardedFor)
    {
        // Rightmost first: the connection address, then the forwarded entries from right to left.
        var chain = new List<string>();
        if (remoteIpAddress is not null)
        {
            chain.Add(remoteIpAddress.ToString());
        }

        var entries = forwardedFor
            .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Reverse();
        chain.AddRange(entries);

        if (chain.Count == 0)
        {
            return "unknown";
        }

        var skipped = 0;
        foreach (var entry in chain)
        {
            if (!TryParseAddress(entry, out var address))
            {
                return Truncate(entry);
            }

            if (skipped >= MaxTrustedHops || !IsTrustedProxy(address))
            {
                return address.ToString();
            }

            skipped++;
        }

        // Every entry is a proxy address (e.g. a request from inside the platform): use the leftmost one.
        return TryParseAddress(chain[^1], out var leftmost) ? leftmost.ToString() : Truncate(chain[^1]);
    }

    internal static bool IsTrustedProxy(IPAddress address)
    {
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return TrustedNetworks.Any(network => network.Contains(normalized));
    }

    private static bool TryParseAddress(string value, out IPAddress address)
    {
        if (IPAddress.TryParse(value, out var parsed))
        {
            address = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed;
            return true;
        }

        // "203.0.113.7:4711" or "[2001:db8::1]:443".
        if (IPEndPoint.TryParse(value, out var endpoint))
        {
            address = endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
            return true;
        }

        address = IPAddress.None;
        return false;
    }

    private static string Truncate(string value) => value.Length <= MaxKeyLength ? value : value[..MaxKeyLength];
}
