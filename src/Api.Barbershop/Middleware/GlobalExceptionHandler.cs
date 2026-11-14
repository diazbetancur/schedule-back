using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Barbershop.Features.Uploads;
using Barbershop.Application.Common.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Net.Sockets;

namespace Api.Barbershop.Middleware;

public sealed partial class GlobalExceptionHandler : IExceptionHandler
{
  /// <summary>BUG-021/036/040/006: a domain ArgumentException never sends its .NET message to the client.</summary>
  public const string InvalidDataMessage = "Hay datos que no son válidos. Revisa la información e inténtalo de nuevo.";

  public const string RequiredMessage = "Este dato es obligatorio.";
  public const string WrongFormatMessage = "El valor no tiene el formato esperado.";
  public const string UnreadableBodyMessage = "No se pudo leer la solicitud: revisa que los datos tengan el formato esperado.";

  /// <summary>BUG-072: concurrent writes used to answer 500.</summary>
  public const string DuplicateMessage = "Ya existe un registro con esos datos. Recarga la página y revisa antes de volver a intentarlo.";

  public const string ConcurrentChangeMessage =
      "Otra persona cambió estos datos al mismo tiempo. Recarga la página e inténtalo de nuevo.";

  private const string UniqueViolation = "23505";
  private const string ForeignKeyViolation = "23503";
  private const string SerializationFailure = "40001";
  private const string DeadlockDetected = "40P01";
  private const string CharacterNotInRepertoire = "22021";

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
      return await WriteValidationAsync(
          httpContext, exception, StatusCodes.Status422UnprocessableEntity, validationProblemException.Errors,
          validationProblemException.Message);
    }

    // A body or a parameter that could not be read (BUG-069, BUG-081, BUG-021): field errors in Spanish.
    if (exception is BadHttpRequestException { InnerException: JsonException jsonException }
        && exception is not BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
    {
      var missing = MissingRequiredProperties(jsonException);
      return missing.Count > 0
          ? await WriteValidationAsync(
              httpContext, exception, StatusCodes.Status422UnprocessableEntity,
              missing.ToDictionary(name => name, _ => new[] { RequiredMessage }, StringComparer.Ordinal),
              "Faltan datos obligatorios.")
          : await WriteValidationAsync(
              httpContext, exception, StatusCodes.Status400BadRequest,
              new Dictionary<string, string[]>(StringComparer.Ordinal) { [FieldOf(jsonException.Path)] = [WrongFormatMessage] },
              UnreadableBodyMessage);
    }

    // BUG-039 safety net: a NUL character that still reached PostgreSQL.
    if (PostgresErrorOf(exception) is { SqlState: CharacterNotInRepertoire })
    {
      return await WriteValidationAsync(
          httpContext, exception, StatusCodes.Status422UnprocessableEntity,
          new Dictionary<string, string[]>(StringComparer.Ordinal) { ["texto"] = [InputValueGuard.NulMessage] },
          InputValueGuard.NulMessage);
    }

    var conflict = ConflictMessageOf(exception);

    var (status, title, code) = conflict is not null
      ? (StatusCodes.Status409Conflict, "Conflict", "conflict")
      : IsInfrastructureUnavailable(exception)
      ? (StatusCodes.Status503ServiceUnavailable, "Service unavailable", "service_unavailable")
      : exception switch
      {
        PayloadTooLargeException => (StatusCodes.Status413PayloadTooLarge, "Payload too large", "payload_too_large"),
        BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
            (StatusCodes.Status413PayloadTooLarge, "Payload too large", "payload_too_large"),
        BadHttpRequestException => (StatusCodes.Status400BadRequest, "Invalid request", "invalid_request"),
        BadRequestException => (StatusCodes.Status400BadRequest, "Invalid request", "invalid_request"),
        ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request", "invalid_request"),
        ConflictException => (StatusCodes.Status409Conflict, "Conflict", "conflict"),
        UnauthorizedException => (StatusCodes.Status401Unauthorized, "Unauthorized", "unauthorized"),
        TooManyRequestsException => (StatusCodes.Status429TooManyRequests, "Too many requests", "too_many_requests"),
        NotFoundException => (StatusCodes.Status404NotFound, "Resource not found", "resource_not_found"),
        KeyNotFoundException => (StatusCodes.Status404NotFound, "Resource not found", "resource_not_found"),
        UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "Forbidden", "forbidden"),
        ServiceUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Service unavailable", "service_unavailable"),
        _ => (StatusCodes.Status500InternalServerError, "Unexpected server error", "server_error")
      };

    var detail = exception switch
    {
      _ when conflict is not null => conflict,
      // Kestrel's own reading errors (BUG-095): fixed Spanish text, the framework message is not for end users.
      BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => UploadRequestBody.TooLargeMessage,
      BadHttpRequestException when httpContext.Request.HasFormContentType => UploadRequestBody.UnreadableMessage,
      BadHttpRequestException => UnreadableBodyMessage,
      ArgumentException => InvalidDataMessage,
      _ when status >= 500 && !_environment.IsDevelopment() => "Ocurrió un error inesperado. Inténtalo de nuevo en unos minutos.",
      _ => exception.Message
    };

    httpContext.Response.StatusCode = status;

    var problemDetails = new ProblemDetails
    {
      Status = status,
      Title = title,
      Detail = detail,
      Instance = httpContext.Request.Path
    };

    problemDetails.Extensions["code"] = code;
    AddCorrelationId(httpContext, problemDetails);

    return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
    {
      HttpContext = httpContext,
      ProblemDetails = problemDetails,
      Exception = exception
    });
  }

  private async ValueTask<bool> WriteValidationAsync(
      HttpContext httpContext, Exception exception, int status, IEnumerable<KeyValuePair<string, string[]>> errors, string detail)
  {
    httpContext.Response.StatusCode = status;

    var problemDetails = new HttpValidationProblemDetails(errors)
    {
      Status = status,
      Title = status == StatusCodes.Status400BadRequest ? "Invalid request" : "Validation failed",
      Detail = detail,
      Instance = httpContext.Request.Path
    };

    problemDetails.Extensions["code"] = status == StatusCodes.Status400BadRequest ? "invalid_request" : "validation_failed";
    AddCorrelationId(httpContext, problemDetails);

    return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
    {
      HttpContext = httpContext,
      ProblemDetails = problemDetails,
      Exception = exception
    });
  }

  private static void AddCorrelationId(HttpContext httpContext, ProblemDetails problemDetails)
  {
    var correlationId = httpContext.GetCorrelationId();
    if (!string.IsNullOrWhiteSpace(correlationId))
    {
      problemDetails.Extensions["correlationId"] = correlationId;
    }
  }

  /// <summary>
  /// The JSON names System.Text.Json lists when required properties are missing ("...missing required properties
  /// including: 'isActive', 'amount'."); empty for any other reading error.
  /// </summary>
  internal static IReadOnlyList<string> MissingRequiredProperties(JsonException exception)
  {
    var message = exception.Message;
    var marker = message.IndexOf("missing required properties", StringComparison.OrdinalIgnoreCase);
    if (marker < 0)
    {
      return [];
    }

    return QuotedNameRegex().Matches(message[marker..]).Select(match => match.Groups[1].Value).Distinct().ToList();
  }

  /// <summary>"$.days[2].isOpen" becomes "days[2].isOpen"; the whole body is "body".</summary>
  internal static string FieldOf(string? jsonPath)
  {
    if (string.IsNullOrWhiteSpace(jsonPath) || jsonPath == "$")
    {
      return "body";
    }

    return jsonPath.StartsWith("$.", StringComparison.Ordinal) ? jsonPath[2..] : jsonPath.TrimStart('$');
  }

  /// <summary>BUG-072: unique, foreign key and concurrency failures of a write are conflicts, not server errors.</summary>
  private static string? ConflictMessageOf(Exception exception)
  {
    if (exception is DbUpdateConcurrencyException)
    {
      return ConcurrentChangeMessage;
    }

    return PostgresErrorOf(exception)?.SqlState switch
    {
      UniqueViolation => DuplicateMessage,
      ForeignKeyViolation or SerializationFailure or DeadlockDetected => ConcurrentChangeMessage,
      _ => null
    };
  }

  private static PostgresException? PostgresErrorOf(Exception exception)
  {
    for (Exception? current = exception; current is not null; current = current.InnerException)
    {
      if (current is PostgresException postgresException)
      {
        return postgresException;
      }
    }

    return null;
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

  [GeneratedRegex("'([^']+)'")]
  private static partial Regex QuotedNameRegex();
}
