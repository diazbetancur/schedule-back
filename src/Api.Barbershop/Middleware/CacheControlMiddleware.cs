using Microsoft.Net.Http.Headers;

namespace Api.Barbershop.Middleware;

/// <summary>
/// One cache policy for every API answer that does not set its own (BUG-101). Responses with personal data (anything
/// authenticated) and every error are never stored; the anonymous public content can be kept for a short time by
/// browsers and Cloudflare. Availability is not cached: it changes as soon as someone books.
/// </summary>
public sealed class CacheControlMiddleware
{
  public const string PublicShortLived = "public, max-age=60";
  public const string NoStore = "no-store";

  private readonly RequestDelegate _next;

  public CacheControlMiddleware(RequestDelegate next)
  {
    _next = next;
  }

  public Task InvokeAsync(HttpContext context)
  {
    context.Response.OnStarting(() =>
    {
      var headers = context.Response.Headers;
      if (string.IsNullOrEmpty(headers.CacheControl))
      {
        headers.CacheControl = IsCacheablePublicRead(context) ? PublicShortLived : NoStore;
      }

      return Task.CompletedTask;
    });

    return _next(context);
  }

  /// <summary>A successful anonymous GET of public content (landing, catalog, public staff profiles and reviews).</summary>
  internal static bool IsCacheablePublicRead(HttpContext context)
  {
    var request = context.Request;
    if (!HttpMethods.IsGet(request.Method)
        || context.Response.StatusCode != StatusCodes.Status200OK
        || request.Headers.ContainsKey(HeaderNames.Authorization))
    {
      return false;
    }

    var path = request.Path.Value ?? string.Empty;
    var publicIndex = path.IndexOf("/public/", StringComparison.OrdinalIgnoreCase);
    return publicIndex >= 0 && !path.Contains("/availability", StringComparison.OrdinalIgnoreCase);
  }
}
