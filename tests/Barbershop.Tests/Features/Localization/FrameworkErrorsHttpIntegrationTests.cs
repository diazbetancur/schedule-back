using System.Net;
using System.Text.Json;
using Barbershop.Tests.Infrastructure;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Localization;

/// <summary>
/// SONDA BUG-007 / BUG-044: the framework's own answers (an id that is not a GUID, a method the route does not have)
/// said «Not Found» / «Method Not Allowed»; they are Spanish now.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "FrameworkErrorsHttpIntegration")]
public sealed class FrameworkErrorsHttpIntegrationTests
{
  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public FrameworkErrorsHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task Route_NotFound_And_MethodNotAllowed_Answer_In_Spanish()
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return;
    }

    using var factory = new IntegrationTestFactory(_postgres.ConnectionString);
    await factory.ResetDatabaseAsync();
    using var client = factory.CreateClient();

    using var notGuid = await client.GetAsync("/api/v1/public/staff/no-es-guid");
    Assert.Equal(HttpStatusCode.NotFound, notGuid.StatusCode);
    var notFound = await ReadAsync(notGuid);
    Assert.Equal("No encontrado", notFound.Title);
    Assert.Equal("No encontramos lo que buscas.", notFound.Detail);

    using var missing = await client.GetAsync($"/api/v1/public/staff/{Guid.NewGuid()}");
    Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    Assert.Equal("No encontramos este profesional.", (await ReadAsync(missing)).Detail);

    using var wrongMethod = await client.DeleteAsync("/api/v1/public/staff");
    Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
    var notAllowed = await ReadAsync(wrongMethod);
    Assert.Equal("Acción no permitida", notAllowed.Title);
    Assert.Equal("Esta acción no está permitida.", notAllowed.Detail);
  }

  private static async Task<(string? Title, string? Detail)> ReadAsync(HttpResponseMessage response)
  {
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var root = document.RootElement;
    return (
      root.TryGetProperty("title", out var title) ? title.GetString() : null,
      root.TryGetProperty("detail", out var detail) ? detail.GetString() : null);
  }
}
