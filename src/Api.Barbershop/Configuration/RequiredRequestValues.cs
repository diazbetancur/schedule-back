using System.Reflection;
using System.Text.Json.Serialization.Metadata;

namespace Api.Barbershop.Configuration;

/// <summary>
/// A value-type property left out of a JSON body used to take its type's default (BUG-069, BUG-081):
/// PATCH .../status with {} deactivated the resource (isActive = false) and an income without amount or occurredOn
/// was stored with 0 COP and 0001-01-01. In every write request (a record named *Request in the application or API
/// assemblies) each constructor parameter of a non-nullable value type without a default value is therefore required:
/// leaving it out answers 422 instead of silently storing the default. Parameters that declare a default
/// (for example <c>bool IsActive = true</c>) and nullable ones keep being optional.
/// </summary>
public static class RequiredRequestValues
{
  private static readonly Assembly[] RequestAssemblies =
  [
    typeof(global::Barbershop.Application.DependencyInjection).Assembly,
    typeof(RequiredRequestValues).Assembly
  ];

  public static void Apply(JsonTypeInfo typeInfo)
  {
    if (typeInfo.Kind != JsonTypeInfoKind.Object || !IsWriteRequest(typeInfo.Type))
    {
      return;
    }

    foreach (var property in typeInfo.Properties)
    {
      if (IsRequiredValue(property))
      {
        property.IsRequired = true;
      }
    }
  }

  /// <summary>The write requests this rule covers; exposed for the test that lists them.</summary>
  public static bool IsWriteRequest(Type type)
      => type.Name.EndsWith("Request", StringComparison.Ordinal)
          && Array.IndexOf(RequestAssemblies, type.Assembly) >= 0;

  public static bool IsRequiredValue(JsonPropertyInfo property)
      => property.AssociatedParameter is { HasDefaultValue: false }
          && property.PropertyType.IsValueType
          && Nullable.GetUnderlyingType(property.PropertyType) is null;
}
