using System.Text.Json;
using Barbershop.Application.Auth;

namespace Api.Barbershop.Features.Auth;

/// <summary>
/// How the refresh token travels between the API and the app: an HttpOnly cookie for every browser and, as a fallback
/// for iOS standalone PWAs where ITP blocks the cross-site cookie, the JSON body.
/// </summary>
internal static class RefreshTokenTransport
{
    public const string CookieName = "X-Refresh-Token";

    /// <summary>A refresh token can never be this long; anything bigger is not read.</summary>
    private const long MaxBodyBytes = 16 * 1024;

    // El refresh token va en cookie HttpOnly (todos los browsers) Y en el body
    // (fallback para iOS standalone donde las cookies cross-origin son bloqueadas por ITP).
    internal sealed record AuthApiTokenResponse(string AccessToken, int ExpiresInSeconds, AuthUserResponse User, string RefreshToken);

    private sealed record RefreshTokenBody(string? RefreshToken);

    /// <summary>The token found in the body, or why there is none.</summary>
    internal readonly record struct BodyToken(string? Token, bool IsMalformed);

    public static AuthApiTokenResponse ToApiResponse(TokenResponse response) =>
        new(response.AccessToken, response.ExpiresInSeconds, response.User, response.RefreshToken);

    public static string? ReadCookie(HttpContext context)
    {
        var value = context.Request.Cookies[CookieName];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Reads <c>{"refreshToken": "..."}</c> from the body without ever throwing: no body, an empty body or a body that
    /// is not JSON means "no token"; a JSON content type with a body that does not parse is reported as malformed.
    /// The token itself is never logged.
    /// </summary>
    public static async Task<BodyToken> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is 0 or > MaxBodyBytes || !request.HasJsonContentType())
        {
            return new BodyToken(null, IsMalformed: false);
        }

        try
        {
            using var reader = new StreamReader(request.Body);
            var buffer = new char[MaxBodyBytes + 1];
            var read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
            if (read > MaxBodyBytes)
            {
                return new BodyToken(null, IsMalformed: true);
            }

            var json = new string(buffer, 0, read);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new BodyToken(null, IsMalformed: false);
            }

            var body = JsonSerializer.Deserialize<RefreshTokenBody>(json, JsonSerializerOptions.Web);
            var token = body?.RefreshToken;
            return new BodyToken(string.IsNullOrWhiteSpace(token) ? null : token, IsMalformed: false);
        }
        catch (JsonException)
        {
            return new BodyToken(null, IsMalformed: true);
        }
    }

    public static void SetCookie(HttpContext context, string refreshToken, int refreshTokenDays)
    {
        var options = BuildCookieOptions(context);
        // iOS WebKit (PWA standalone) requiere Expires explícito — Max-Age solo
        // hace que la cookie se trate como de sesión y se borre al cerrar la app.
        var expiry = DateTimeOffset.UtcNow.AddDays(refreshTokenDays);
        options.MaxAge = TimeSpan.FromDays(refreshTokenDays);
        options.Expires = expiry;
        context.Response.Cookies.Append(CookieName, refreshToken, options);
    }

    public static void ClearCookie(HttpContext context)
    {
        context.Response.Cookies.Delete(CookieName, BuildCookieOptions(context));
    }

    // localhost is treated as a secure context by modern browsers, so SameSite=None; Secure
    // works for cross-port dev traffic (localhost:4200 → localhost:5000). In production the
    // app runs over HTTPS, which is also required by the PWA manifest.
    private static CookieOptions BuildCookieOptions(HttpContext context)
    {
        var isSecureContext = context.Request.IsHttps
            || context.Request.Host.Host is "localhost" or "127.0.0.1";

        return new CookieOptions
        {
            HttpOnly = true,
            Secure = isSecureContext,
            SameSite = isSecureContext ? SameSiteMode.None : SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
        };
    }
}
