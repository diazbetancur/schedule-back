using Barbershop.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Api.Barbershop.Middleware;

public sealed class CorrelationIdMiddleware
{
  private const string CorrelationIdItemKey = "CorrelationId";

  private readonly RequestDelegate _next;

  public CorrelationIdMiddleware(RequestDelegate next)
  {
    _next = next;
  }

  public async Task InvokeAsync(HttpContext context, IOptions<AppOptions> appOptions)
  {
    var headerName = appOptions.Value.CorrelationIdHeaderName;
    var correlationId = context.Request.Headers.TryGetValue(headerName, out var incomingValues)
        && !string.IsNullOrWhiteSpace(incomingValues.ToString())
            ? incomingValues.ToString()
            : context.TraceIdentifier;

    context.Items[CorrelationIdItemKey] = correlationId;
    context.Response.Headers[headerName] = correlationId;

    // BUG-089: the exception handler clears the response (headers included) before writing an error, so the header
    // set above was lost on every error. Set it again right before the response starts, whoever wrote it.
    context.Response.OnStarting(() =>
    {
      context.Response.Headers[headerName] = correlationId;
      return Task.CompletedTask;
    });

    await _next(context);
  }

  public static string? GetCorrelationId(HttpContext context)
      => context.Items.TryGetValue(CorrelationIdItemKey, out var correlationId)
          ? correlationId?.ToString()
          : null;
}

public static class CorrelationIdHttpContextExtensions
{
  public static string? GetCorrelationId(this HttpContext context)
      => CorrelationIdMiddleware.GetCorrelationId(context);
}