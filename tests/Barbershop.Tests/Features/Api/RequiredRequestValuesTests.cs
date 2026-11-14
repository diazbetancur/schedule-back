using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Api.Barbershop.Configuration;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Api;

/// <summary>BUG-069 / BUG-081: value-type properties of write requests cannot be left out of the JSON body.</summary>
public sealed class RequiredRequestValuesTests(ITestOutputHelper output)
{
  private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
  {
    TypeInfoResolver = new DefaultJsonTypeInfoResolver().WithAddedModifier(RequiredRequestValues.Apply)
  };

  public static IEnumerable<(Type Type, string Property)> RequiredProperties()
  {
    var assemblies = new[]
    {
      typeof(global::Barbershop.Application.DependencyInjection).Assembly,
      typeof(RequiredRequestValues).Assembly
    };

    foreach (var type in assemblies.SelectMany(assembly => assembly.GetTypes())
                 .Where(type => !type.IsAbstract && !type.IsGenericTypeDefinition && RequiredRequestValues.IsWriteRequest(type))
                 .OrderBy(type => type.Name, StringComparer.Ordinal))
    {
      JsonTypeInfo info;
      try
      {
        info = Options.GetTypeInfo(type);
      }
      catch (InvalidOperationException)
      {
        continue; // Not a JSON object (no usable constructor): never read from a body.
      }

      foreach (var property in info.Properties.Where(property => property.IsRequired))
      {
        yield return (type, property.Name);
      }
    }
  }

  [Fact]
  public void ListsTheRequiredValueProperties()
  {
    foreach (var (type, property) in RequiredProperties())
    {
      output.WriteLine($"{type.Name}.{property}");
    }

    Assert.NotEmpty(RequiredProperties());
  }
}
