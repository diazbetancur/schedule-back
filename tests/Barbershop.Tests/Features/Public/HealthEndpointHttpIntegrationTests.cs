using System.Net;
using Barbershop.Tests.Infrastructure;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Public;

/// <summary>SONDA BUG-054: the health probes answer GET and HEAD only; any other method is 405, like /ping and /.</summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "HealthEndpointHttpIntegration")]
public sealed class HealthEndpointHttpIntegrationTests
{
  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public HealthEndpointHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  public static TheoryData<string> ProbePaths => new() { "/health", "/api/v1/health", "/ping" };

  [Theory]
  [MemberData(nameof(ProbePaths))]
  public async Task Probe_AcceptsGetAndHeadOnly(string path)
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return;
    }

    using var factory = new IntegrationTestFactory(_postgres.ConnectionString);
    await factory.ResetDatabaseAsync();
    using var client = factory.CreateClient();

    foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
    {
      using var response = await client.SendAsync(new HttpRequestMessage(method, path));
      Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method} {path} returned {(int)response.StatusCode}");
    }

    foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
    {
      using var response = await client.SendAsync(new HttpRequestMessage(method, path));
      Assert.True(
          response.StatusCode == HttpStatusCode.MethodNotAllowed,
          $"{method} {path} returned {(int)response.StatusCode}, expected 405");
    }
  }
}
