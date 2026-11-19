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

  /// <summary>SONDA BUG-160: amounts and other whole numbers are 32-bit; the limit is technical, not a business cap.</summary>
  public const string IntegerOutOfRangeMessage = "Debe ser un número entero de como máximo 2.147.483.647.";
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
      if (missing.Count == 0 && IsIntegerConversion(jsonException) && await IsNumberAtAsync(httpContext, jsonException.Path))
      {
        // SONDA BUG-160: 2147483648 in «amount» used to be a generic 400; it is a value the field cannot hold.
        return await WriteValidationAsync(
            httpContext, exception, StatusCodes.Status422UnprocessableEntity,
            new Dictionary<string, string[]>(StringComparer.Ordinal) { [FieldOf(jsonException.Path)] = [IntegerOutOfRangeMessage] },
            IntegerOutOfRangeMessage);
      }

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

    // SONDA BUG-141/158: a whole number in the query that does not fit (page=2147483648) is a field error, not a
    // generic 400: «Failed to bind parameter "int? page" from "2147483648".»
    if (exception is BadHttpRequestException { InnerException: null } bindException
        && BoundIntegerParameter(bindException.Message) is { } parameter)
    {
      return await WriteValidationAsync(
          httpContext, exception, StatusCodes.Status422UnprocessableEntity,
          new Dictionary<string, string[]>(StringComparer.Ordinal) { [parameter] = [IntegerOutOfRangeMessage] },
          IntegerOutOfRangeMessage);
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
        GoneException => (StatusCodes.Status410Gone, "Gone", "gone"),
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
  /// <summary>The name of an int query or route parameter that could not be bound, from the framework message.</summary>
  internal static string? BoundIntegerParameter(string message)
  {
    // «Failed to bind parameter "int? page" from …» or, through [AsParameters], «"Nullable<int> Page"».
    var match = System.Text.RegularExpressions.Regex.Match(message, @"^Failed to bind parameter ""(?<type>[^""]+) (?<name>[A-Za-z0-9_]+)"" from ");
    if (!match.Success || !System.Text.RegularExpressions.Regex.IsMatch(match.Groups["type"].Value, @"^(int\??|Nullable<int>|Int32|Nullable<Int32>)$"))
    {
      return null;
    }

    var name = match.Groups["name"].Value;
    return char.ToLowerInvariant(name[0]) + name[1..];
  }

  /// <summary>The body had a value that could not be read as a 32-bit whole number; <see cref="IsNumberAtAsync"/> tells an overflow.</summary>
  /// A value of another type (a text) is not this case: it stays a 400 «formato esperado» (lote C2).
  internal static bool IsIntegerConversion(JsonException exception)
      => exception.Message.Contains("System.Int32", StringComparison.Ordinal)
          || exception.InnerException is FormatException { Message: var inner } && inner.Contains("Int32", StringComparison.Ordinal);

  /// <summary>Whether the (buffered) JSON body has a number at <paramref name="path"/> («$.amount», «$.lines[0].quantity»).</summary>
  internal const string BufferedJsonBodyKey = "barbershop.buffered-json-body";

  internal static async Task<bool> IsNumberAtAsync(HttpContext httpContext, string? path)
  {
    var body = httpContext.Items[BufferedJsonBodyKey] as Stream ?? httpContext.Request.Body;
    if (path is null || !body.CanSeek)
    {
      return false;
    }

    try
    {
      body.Position = 0;
      using var document = await JsonDocument.ParseAsync(body);
      var element = document.RootElement;
      foreach (System.Text.RegularExpressions.Match step in System.Text.RegularExpressions.Regex.Matches(path, @"\.([^.\[]+)|\[(\d+)\]"))
      {
        if (step.Groups[1].Success)
        {
          if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(step.Groups[1].Value, out element))
          {
            return false;
          }
        }
        else if (element.ValueKind != JsonValueKind.Array || int.Parse(step.Groups[2].Value) >= element.GetArrayLength())
        {
          return false;
        }
        else
        {
          element = element[int.Parse(step.Groups[2].Value)];
        }
      }

      // Only a whole number that does not fit (a fraction is the wrong format, as before: 400).
      return element.ValueKind == JsonValueKind.Number && !element.TryGetInt32(out _) && element.TryGetDecimal(out var number)
          && number == decimal.Truncate(number) && !element.GetRawText().Contains('.');
    }
    catch (JsonException)
    {
      return false;
    }
  }

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
