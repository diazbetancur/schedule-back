namespace Api.Barbershop.Middleware;

// Adds defense-in-depth headers for an API that only ever returns JSON/problem+json.
// The SPA (served separately) carries its own CSP/HSTS tuned for rendering HTML.
public sealed class SecurityHeadersMiddleware
{
  private readonly RequestDelegate _next;
  private readonly bool _isDevelopment;

  public SecurityHeadersMiddleware(RequestDelegate next, IHostEnvironment environment)
  {
    _next = next;
    _isDevelopment = environment.IsDevelopment();
  }

  public Task InvokeAsync(HttpContext context)
  {
    context.Response.OnStarting(() =>
    {
      var headers = context.Response.Headers;

      headers["X-Content-Type-Options"] = "nosniff";
      headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
      headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";

      if (!_isDevelopment && context.Request.IsHttps)
      {
        headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
      }

      return Task.CompletedTask;
    });

    return _next(context);
  }
}
