using System.Net;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Tests.Infrastructure;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Auth;

/// <summary>
/// SONDA BUG-117: POST /auth/forgot-password answers 429 from the 4th request per client within a minute and login
/// from the 6th, also when the client rotates the addresses it forges in X-Forwarded-For.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "AuthRateLimitHttpIntegration")]
public sealed class AuthRateLimitHttpIntegrationTests
{
  // As in production: [forged by the client], real client, Cloudflare edge (the platform proxy is the connection).
  private const string RealClient = "203.0.113.7";
  private const string CloudflareEdge = "172.70.1.1";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public AuthRateLimitHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task ForgotPassword_Answers429_FromTheFourthRequest_EvenRotatingForgedAddresses()
  {
    using var context = await CreateAsync();
    if (context is null) return;

    var statuses = new List<HttpStatusCode>();
    for (var attempt = 1; attempt <= 6; attempt++)
    {
      using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/forgot-password")
      {
        Content = JsonContent.Create(new ForgotPasswordRequest($"nadie-{Guid.NewGuid():N}@example.com")),
      };
      // A different forged address on every request: it must not open a new quota.
      request.Headers.TryAddWithoutValidation("X-Forwarded-For", $"198.51.100.{attempt}, {RealClient}, {CloudflareEdge}");
      using var response = await context.Value.Client.SendAsync(request);
      statuses.Add(response.StatusCode);
      if (response.StatusCode == HttpStatusCode.TooManyRequests)
      {
        Assert.NotNull(response.Headers.RetryAfter);
      }
    }

    _output.WriteLine(string.Join(", ", statuses.Select(status => (int)status)));
    Assert.All(statuses.Take(3), status => Assert.NotEqual(HttpStatusCode.TooManyRequests, status));
    Assert.All(statuses.Skip(3), status => Assert.Equal(HttpStatusCode.TooManyRequests, status));
  }

  [Fact]
  public async Task Login_Answers429_FromTheSixthRequest()
  {
    using var context = await CreateAsync();
    if (context is null) return;

    var statuses = new List<HttpStatusCode>();
    for (var attempt = 1; attempt <= 7; attempt++)
    {
      using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
      {
        Content = JsonContent.Create(new LoginRequest("nadie@example.com", "NoEsLaClave1!")),
      };
      request.Headers.TryAddWithoutValidation("X-Forwarded-For", $"198.51.100.{attempt}, {RealClient}, {CloudflareEdge}");
      using var response = await context.Value.Client.SendAsync(request);
      statuses.Add(response.StatusCode);
    }

    _output.WriteLine(string.Join(", ", statuses.Select(status => (int)status)));
    Assert.All(statuses.Take(5), status => Assert.Equal(HttpStatusCode.Unauthorized, status));
    Assert.All(statuses.Skip(5), status => Assert.Equal(HttpStatusCode.TooManyRequests, status));
  }

  [Fact]
  public async Task Another_Real_Client_Keeps_Its_Own_Quota()
  {
    using var context = await CreateAsync();
    if (context is null) return;

    for (var attempt = 1; attempt <= 4; attempt++)
    {
      using var spent = await SendForgotAsync(context.Value.Client, $"{RealClient}, {CloudflareEdge}");
    }

    using var other = await SendForgotAsync(context.Value.Client, $"203.0.113.99, {CloudflareEdge}");
    Assert.NotEqual(HttpStatusCode.TooManyRequests, other.StatusCode);
  }

  private static async Task<HttpResponseMessage> SendForgotAsync(HttpClient client, string forwardedFor)
  {
    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/forgot-password")
    {
      Content = JsonContent.Create(new ForgotPasswordRequest($"nadie-{Guid.NewGuid():N}@example.com")),
    };
    request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
    return await client.SendAsync(request);
  }

  private async Task<Context?> CreateAsync()
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return null;
    }

    var factory = new IntegrationTestFactory(_postgres.ConnectionString);
    await factory.ResetDatabaseAsync();
    return new Context(factory, factory.CreateClient());
  }

  private readonly record struct Context(IntegrationTestFactory Factory, HttpClient Client) : IDisposable
  {
    public void Dispose()
    {
      Client.Dispose();
      Factory.Dispose();
    }
  }
}
