using System.Collections;
using System.Reflection;
using System.Text.Json;
using Barbershop.Application.Common.Exceptions;

namespace Api.Barbershop.Middleware;

/// <summary>
/// One check for every value an endpoint receives (body DTOs, [AsParameters] records, query and route values), before
/// any service sees it:
/// <list type="bullet">
/// <item>BUG-039: text with the NUL character (U+0000) reached PostgreSQL, which rejects it in text columns, and the
/// request answered 500 in any endpoint (some anonymous).</item>
/// <item>BUG-040: dates such as 0001-01-01 or 9999-12-31 overflowed date arithmetic and answered 400 with the .NET
/// message. Dates must fall between 1900 and 2100.</item>
/// </list>
/// Both answer 422 with the field, in Spanish.
/// </summary>
public static class InputValueGuard
{
  public const string NulMessage = "El texto contiene caracteres no permitidos.";
  public const string DateRangeMessage = "La fecha está fuera del rango permitido (entre 1900 y 2100).";

  /// <summary>SONDA BUG-126: a calendar date in the URL with a time («2026-11-10T23:00:00») used to be read silently.</summary>
  public const string DateOnlyFormatMessage = "Escribe la fecha como AAAA-MM-DD, sin hora.";

  public static readonly DateOnly MinDate = new(1900, 1, 1);
  public static readonly DateOnly MaxDate = new(2100, 12, 31);

  private const int MaxDepth = 4;

  public static EndpointFilterDelegate Create(EndpointFilterFactoryContext factoryContext, EndpointFilterDelegate next)
  {
    var parameters = factoryContext.MethodInfo.GetParameters();

    return async invocationContext =>
    {
      var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
      var datePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      for (var index = 0; index < invocationContext.Arguments.Count && index < parameters.Length; index++)
      {
        var value = invocationContext.Arguments[index];
        var parameter = parameters[index];
        var isComplex = value is not null && IsInspectable(value.GetType());

        // A body DTO or [AsParameters] record reports its own property names; a simple value its parameter name.
        Inspect(value, isComplex ? string.Empty : JsonNamingPolicy.CamelCase.ConvertName(parameter.Name ?? "value"), errors, 0, datePaths);
      }

      // A calendar date that came in the query string must be exactly a date.
      var query = invocationContext.HttpContext.Request.Query;
      foreach (var path in datePaths)
      {
        if (query.TryGetValue(path, out var raw) && raw.Count > 0
            && !System.Text.RegularExpressions.Regex.IsMatch(raw[0] ?? string.Empty, @"^\d{4}-\d{2}-\d{2}$"))
        {
          errors.TryAdd(path, [DateOnlyFormatMessage]);
        }
      }

      if (errors.Count > 0)
      {
        throw new ValidationProblemException(errors);
      }

      return await next(invocationContext);
    };
  }

  private static void Inspect(object? value, string path, Dictionary<string, string[]> errors, int depth, HashSet<string>? datePaths = null)
  {
    switch (value)
    {
      case null:
        return;
      case string text:
        if (text.Contains('\0', StringComparison.Ordinal))
        {
          errors.TryAdd(Key(path), [NulMessage]);
        }

        return;
      case DateOnly date:
        CheckDate(date, path, errors);
        datePaths?.Add(Key(path));
        return;
      case DateTime dateTime:
        CheckDate(DateOnly.FromDateTime(dateTime), path, errors);
        return;
      case DateTimeOffset dateTimeOffset:
        CheckDate(DateOnly.FromDateTime(dateTimeOffset.UtcDateTime), path, errors);
        return;
    }

    if (depth >= MaxDepth)
    {
      return;
    }

    var type = value.GetType();
    if (value is IEnumerable items && IsPlainCollection(type))
    {
      var index = 0;
      foreach (var item in items)
      {
        Inspect(item, $"{path}[{index}]", errors, depth + 1, datePaths);
        index++;
      }

      return;
    }

    if (!IsInspectable(type))
    {
      return;
    }

    foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
    {
      if (property.GetIndexParameters().Length > 0 || !property.CanRead)
      {
        continue;
      }

      var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
      Inspect(property.GetValue(value), string.IsNullOrEmpty(path) ? name : $"{path}.{name}", errors, depth + 1, datePaths);
    }
  }

  private static void CheckDate(DateOnly date, string path, Dictionary<string, string[]> errors)
  {
    if (date < MinDate || date > MaxDate)
    {
      errors.TryAdd(Key(path), [DateRangeMessage]);
    }
  }

  private static string Key(string path) => string.IsNullOrEmpty(path) ? "value" : path;

  private static readonly Assembly[] DtoAssemblies =
  [
    typeof(global::Barbershop.Application.DependencyInjection).Assembly,
    typeof(InputValueGuard).Assembly
  ];

  /// <summary>
  /// Only request records and DTOs of the application and API assemblies. Injected services (a DbContext would run
  /// queries if its sets were enumerated), framework types and streams are never inspected.
  /// </summary>
  private static bool IsInspectable(Type type)
      => type.IsClass
          && Array.IndexOf(DtoAssemblies, type.Assembly) >= 0
          && (IsRecord(type)
              || type.Name.EndsWith("Request", StringComparison.Ordinal)
              || type.Name.EndsWith("Query", StringComparison.Ordinal));

  private static bool IsRecord(Type type) => type.GetMethod("<Clone>$") is not null;

  /// <summary>Arrays and the System.Collections.Generic collections a JSON body binds to.</summary>
  private static bool IsPlainCollection(Type type)
      => type.IsArray || type.Namespace == "System.Collections.Generic";
}
