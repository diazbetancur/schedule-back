using Microsoft.AspNetCore.Mvc;

namespace Api.Barbershop.Middleware;

public static class ProblemDetailsExtensions
{
  public static void CustomizeProblemDetails(ProblemDetailsContext context, IHostEnvironment environment)
  {
    var problemDetails = context.ProblemDetails;
    var status = problemDetails.Status ?? context.HttpContext.Response.StatusCode;

    problemDetails.Status = status;
    problemDetails.Title ??= GetTitle(status);
    problemDetails.Instance ??= context.HttpContext.Request.Path;

    if (status >= StatusCodes.Status500InternalServerError && !environment.IsDevelopment() && string.IsNullOrWhiteSpace(problemDetails.Detail))
    {
      problemDetails.Detail = "An unexpected error occurred while processing the request.";
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

  private static string GetTitle(int statusCode) => statusCode switch
  {
    StatusCodes.Status400BadRequest => "Bad request",
    StatusCodes.Status401Unauthorized => "Unauthorized",
    StatusCodes.Status403Forbidden => "Forbidden",
    StatusCodes.Status404NotFound => "Not found",
    StatusCodes.Status409Conflict => "Conflict",
    StatusCodes.Status422UnprocessableEntity => "Validation failed",
    _ => "Unexpected server error"
  };

  private static string GetCode(int statusCode) => statusCode switch
  {
    StatusCodes.Status400BadRequest => "bad_request",
    StatusCodes.Status401Unauthorized => "unauthorized",
    StatusCodes.Status403Forbidden => "forbidden",
    StatusCodes.Status404NotFound => "not_found",
    StatusCodes.Status409Conflict => "conflict",
    StatusCodes.Status422UnprocessableEntity => "validation_failed",
    _ => "server_error"
  };
}