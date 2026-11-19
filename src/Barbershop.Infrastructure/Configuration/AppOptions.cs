using System.ComponentModel.DataAnnotations;

namespace Barbershop.Infrastructure.Configuration;

public sealed class AppOptions
{
    public const string SectionName = "App";

    [Required]
    public string Name { get; init; } = "Barbershop API";

    [Required]
    public string Version { get; init; } = "0.2.0";

    [Required]
    public string ApiBasePath { get; init; } = "/api/v1";

    [Required]
    public string CorrelationIdHeaderName { get; init; } = "X-Correlation-ID";

    /// <summary>
    /// Absolute URL of the app the emails link to (the password reset). Required and https outside Development/Testing,
    /// validated on start: empty, it produced a relative link (/auth/reset-password?token=…) that no mail client opens.
    /// </summary>
    public string FrontendUrl { get; init; } = string.Empty;

    public const string FrontendUrlRequiredMessage =
        "App:FrontendUrl must be an absolute https URL (for example https://zona8.app) outside Development/Testing.";

    /// <summary>An absolute http(s) URL without query or fragment; https only when <paramref name="requireHttps"/>.</summary>
    public static bool IsValidFrontendUrl(string? value, bool requireHttps)
        => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttps || (!requireHttps && uri.Scheme == Uri.UriSchemeHttp))
           && !string.IsNullOrEmpty(uri.Host)
           && string.IsNullOrEmpty(uri.Query)
           && string.IsNullOrEmpty(uri.Fragment);

    /// <summary>
    /// The link of a password reset email. It is always absolute: a missing or relative <see cref="FrontendUrl"/> is a
    /// configuration error, never a link that does not open.
    /// </summary>
    public static string BuildFrontendLink(string? frontendUrl, string pathAndQuery)
    {
        if (!IsValidFrontendUrl(frontendUrl, requireHttps: false))
        {
            throw new InvalidOperationException(FrontendUrlRequiredMessage);
        }

        return $"{frontendUrl!.Trim().TrimEnd('/')}/{pathAndQuery.TrimStart('/')}";
    }
}
