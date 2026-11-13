using Microsoft.AspNetCore.Mvc;

namespace Api.Barbershop.Middleware;

public static class ProblemDetailsExtensions
{
  /// <summary>SONDA BUG-007: text for a 500 outside development (the exception message never reaches the client).</summary>
  public const string UnexpectedErrorMessage = "Ocurrió un error inesperado. Inténtalo de nuevo en unos minutos.";

  public static void CustomizeProblemDetails(ProblemDetailsContext context, IHostEnvironment environment)
  {
    var problemDetails = context.ProblemDetails;
    var status = problemDetails.Status ?? context.HttpContext.Response.StatusCode;

    problemDetails.Status = status;
    // SONDA BUG-007: every answer the client may show is in Spanish. The title is a fixed label per status (the
    // framework's own 404/405 said «Not Found» / «Method Not Allowed»); the machine-readable value stays in "code".
    problemDetails.Title = GetTitle(status);
    problemDetails.Instance ??= context.HttpContext.Request.Path;

    if (status >= StatusCodes.Status500InternalServerError && !environment.IsDevelopment() && string.IsNullOrWhiteSpace(problemDetails.Detail))
    {
      problemDetails.Detail = UnexpectedErrorMessage;
    }

    if (string.IsNullOrWhiteSpace(problemDetails.Detail))
    {
      problemDetails.Detail = GetDefaultDetail(status);
    }

    if (!problemDetails.Extensions.ContainsKey("code"))
    {
      problemDetails.Extensions["code"] = GetCode(status);
    }

    var correlationId = context.HttpContext.GetCorrelationId();
    if (!string.IsNullOrWhiteSpace(correlationId))
    {
      problemDetails.Extensions["correlationId"] = correlationId;
    }
  }

  public static string GetTitle(int statusCode) => statusCode switch
  {
    StatusCodes.Status400BadRequest => "Solicitud no válida",
    StatusCodes.Status401Unauthorized => "No autorizado",
    StatusCodes.Status403Forbidden => "Acceso denegado",
    StatusCodes.Status404NotFound => "No encontrado",
    StatusCodes.Status405MethodNotAllowed => "Acción no permitida",
    StatusCodes.Status409Conflict => "Conflicto",
    StatusCodes.Status413PayloadTooLarge => "Archivo demasiado grande",
    StatusCodes.Status415UnsupportedMediaType => "Formato no admitido",
    StatusCodes.Status422UnprocessableEntity => "Datos no válidos",
    StatusCodes.Status429TooManyRequests => "Demasiadas solicitudes",
    StatusCodes.Status503ServiceUnavailable => "Servicio no disponible",
    _ when statusCode >= 500 => "Error del servidor",
    _ => "Error",
  };

  /// <summary>Spanish detail when the answer carries none (framework 404/405/415, empty problems).</summary>
  public static string GetDefaultDetail(int statusCode) => statusCode switch
  {
    StatusCodes.Status400BadRequest => "La solicitud no es válida. Revisa los datos e inténtalo de nuevo.",
    StatusCodes.Status401Unauthorized => "Inicia sesión para continuar.",
    StatusCodes.Status403Forbidden => "No tienes permiso para hacer esto.",
    StatusCodes.Status404NotFound => "No encontramos lo que buscas.",
    StatusCodes.Status405MethodNotAllowed => "Esta acción no está permitida.",
    StatusCodes.Status409Conflict => "Los datos cambiaron mientras trabajabas. Recarga la página e inténtalo de nuevo.",
    StatusCodes.Status413PayloadTooLarge => "El archivo es demasiado grande para subirlo.",
    StatusCodes.Status415UnsupportedMediaType => "El formato de la solicitud no es el esperado.",
    StatusCodes.Status422UnprocessableEntity => "Hay datos que no son válidos. Revisa los campos marcados.",
    StatusCodes.Status429TooManyRequests => "Hiciste demasiadas solicitudes seguidas. Espera un momento y vuelve a intentarlo.",
    StatusCodes.Status503ServiceUnavailable => "El servicio no está disponible en este momento. Inténtalo de nuevo en unos minutos.",
    _ when statusCode >= 500 => UnexpectedErrorMessage,
    _ => "No pudimos completar la acción.",
  };

  private static string GetCode(int statusCode) => statusCode switch
  {
    StatusCodes.Status400BadRequest => "bad_request",
    StatusCodes.Status401Unauthorized => "unauthorized",
    StatusCodes.Status403Forbidden => "forbidden",
    StatusCodes.Status404NotFound => "not_found",
    StatusCodes.Status405MethodNotAllowed => "method_not_allowed",
    StatusCodes.Status409Conflict => "conflict",
    StatusCodes.Status422UnprocessableEntity => "validation_failed",
    StatusCodes.Status429TooManyRequests => "too_many_requests",
    _ => "server_error"
  };
}
