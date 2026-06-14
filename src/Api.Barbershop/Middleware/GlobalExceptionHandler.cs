using Barbershop.Application.Common.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Net.Sockets;

namespace Api.Barbershop.Middleware;

public sealed class GlobalExceptionHandler : IExceptionHandler
{
  private readonly IProblemDetailsService _problemDetailsService;
  private readonly IHostEnvironment _environment;
  private readonly ILogger<GlobalExceptionHandler> _logger;

  public GlobalExceptionHandler(
      IProblemDetailsService problemDetailsService,
      IHostEnvironment environment,
      ILogger<GlobalExceptionHandler> logger)
  {
    _problemDetailsService = problemDetailsService;
    _environment = environment;
    _logger = logger;
  }

  public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
  {
    _logger.LogError(exception, "Unhandled exception while processing {Path}", httpContext.Request.Path);

    if (exception is ValidationProblemException validationProblemException)
    {
      httpContext.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;

      var validationProblemDetails = new HttpValidationProblemDetails(validationProblemException.Errors)
      {
        Status = StatusCodes.Status422UnprocessableEntity,
        Title = "Validation failed",
        Detail = validationProblemException.Message,
        Instance = httpContext.Request.Path
      };

      validationProblemDetails.Extensions["code"] = "validation_failed";

      var validationCorrelationId = httpContext.GetCorrelationId();
      if (!string.IsNullOrWhiteSpace(validationCorrelationId))
      {
        validationProblemDetails.Extensions["correlationId"] = validationCorrelationId;
      }

      return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
      {
        HttpContext = httpContext,
        ProblemDetails = validationProblemDetails,
        Exception = exception
      });
    }

    var (status, title, code) = IsInfrastructureUnavailable(exception)
      ? (StatusCodes.Status503ServiceUnavailable, "Service unavailable", "service_unavailable")
      : exception switch
      {
        ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request", "invalid_request"),
        ConflictException => (StatusCodes.Status409Conflict, "Conflict", "conflict"),
        UnauthorizedException => (StatusCodes.Status401Unauthorized, "Unauthorized", "unauthorized"),
        NotFoundException => (StatusCodes.Status404NotFound, "Resource not found", "resource_not_found"),
        KeyNotFoundException => (StatusCodes.Status404NotFound, "Resource not found", "resource_not_found"),
        UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "Forbidden", "forbidden"),
        ServiceUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Service unavailable", "service_unavailable"),
        _ => (StatusCodes.Status500InternalServerError, "Unexpected server error", "server_error")
      };

    var detail = status >= 500 && !_environment.IsDevelopment()
        ? "An unexpected error occurred while processing the request."
        : exception.Message;

    httpContext.Response.StatusCode = status;

    var problemDetails = new ProblemDetails
    {
      Status = status,
      Title = title,
      Detail = detail,
      Instance = httpContext.Request.Path
    };

    problemDetails.Extensions["code"] = code;

    var correlationId = httpContext.GetCorrelationId();
    if (!string.IsNullOrWhiteSpace(correlationId))
    {
      problemDetails.Extensions["correlationId"] = correlationId;
    }

    return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
    {
      HttpContext = httpContext,
      ProblemDetails = problemDetails,
      Exception = exception
    });
  }

  private static bool IsInfrastructureUnavailable(Exception exception)
      => exception switch
      {
        SocketException => true,
        TimeoutException => true,
        NpgsqlException npgsqlException => npgsqlException.IsTransient || npgsqlException.InnerException is SocketException,
        InvalidOperationException { InnerException: NpgsqlException npgsqlException } => npgsqlException.IsTransient || npgsqlException.InnerException is SocketException,
        InvalidOperationException { InnerException: SocketException } => true,
        _ => false
      };
}
