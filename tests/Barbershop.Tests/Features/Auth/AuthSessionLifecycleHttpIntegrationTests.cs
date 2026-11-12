using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Barbershop.Application.Auth;
using Barbershop.Application.Customer;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Auth;

/// <summary>
/// Session lifecycle against a disposable PostgreSQL (SONDA BUG-002, BUG-003, BUG-015, BUG-016) and the client key
/// of the auth rate limiter (BUG-001). The clients do not keep cookies, so every token travels in the body: the
/// iOS standalone PWA case. Each test sends its own X-Forwarded-For so it gets its own rate-limit bucket.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "AuthSessionLifecycleHttpIntegration")]
public sealed class AuthSessionLifecycleHttpIntegrationTests
{
  private const string Password = "Secret123!";
  private const string CloudflareEdge = "104.16.0.10";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public AuthSessionLifecycleHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  // ── BUG-003: /auth/refresh never answers 500 ───────────────────────

  [Fact]
  public async Task Refresh_WithUnusableBodies_AnswersWithoutServerErrors()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    using var noBody = await SendAsync(context, HttpMethod.Post, "/api/v1/auth/refresh", null);
    using var emptyJson = await SendAsync(context, HttpMethod.Post, "/api/v1/auth/refresh", Json(""));
    using var plainText = await SendAsync(context, HttpMethod.Post, "/api/v1/auth/refresh",
        new StringContent("esto no es json", Encoding.UTF8, "text/plain"));
    using var malformed = await SendAsync(context, HttpMethod.Post, "/api/v1/auth/refresh", Json("esto no es json"));
    using var wrongShape = await SendAsync(context, HttpMethod.Post, "/api/v1/auth/refresh", Json("[1,2]"));
    using var emptyToken = await SendAsync(context, HttpMethod.Post, "/api/v1/auth/refresh", Json("{\"refreshToken\":\"\"}"));
    using var unknownToken = await SendAsync(context, HttpMethod.Post, "/api/v1/auth/refresh", Json("{\"refreshToken\":\"abc123\"}"));

    Assert.Equal(HttpStatusCode.Unauthorized, noBody.StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, emptyJson.StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, plainText.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, wrongShape.StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, emptyToken.StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, unknownToken.StatusCode);
    Assert.DoesNotContain("esto no es json", await malformed.Content.ReadAsStringAsync());
  }

  // ── BUG-002: logout revokes the body token too ─────────────────────

  [Fact]
  public async Task Logout_WithTheTokenInTheBodyOnly_RevokesIt()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var session = await RegisterAsync(context, "body-logout");

    using var logout = await SendAsync(context, HttpMethod.Post, "/api/v1/auth/logout",
        TokenBody(session.RefreshToken), session.AccessToken);
    Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

    using var refresh = await RefreshAsync(context, session.RefreshToken);
    Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
  }

  [Fact]
  public async Task Logout_WithAnotherAccountsToken_DoesNotRevokeThatSession()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var victim = await RegisterAsync(context, "victim");
    var attacker = await RegisterAsync(context, "attacker");

    using var logout = await SendAsync(context, HttpMethod.Post, "/api/v1/auth/logout",
        TokenBody(victim.RefreshToken), attacker.AccessToken);
    Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

    using var victimRefresh = await RefreshAsync(context, victim.RefreshToken);
    Assert.Equal(HttpStatusCode.OK, victimRefresh.StatusCode);
  }

  [Fact]
  public async Task Logout_WithEmptyOrUnreadableBodies_StillSignsOut()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var session = await RegisterAsync(context, "odd-bodies");

    using var noBody = await SendAsync(context, HttpMethod.Post, "/api/v1/auth/logout", null, session.AccessToken);
    using var emptyObject = await SendAsync(context, HttpMethod.Post, "/api/v1/auth/logout", Json("{}"), session.AccessToken);
    using var malformed = await SendAsync(context, HttpMethod.Post, "/api/v1/auth/logout", Json("no json"), session.AccessToken);

    Assert.Equal(HttpStatusCode.NoContent, noBody.StatusCode);
    Assert.Equal(HttpStatusCode.NoContent, emptyObject.StatusCode);
    Assert.Equal(HttpStatusCode.NoContent, malformed.StatusCode);
  }

  [Fact]
  public async Task Logout_WithAStaleTokenOfTheSession_AlsoRevokesTheTokenItWasRotatedInto()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var session = await RegisterAsync(context, "stale-logout");
    var rotated = await ReadSessionAsync(await RefreshAsync(context, session.RefreshToken));

    using var logout = await SendAsync(context, HttpMethod.Post, "/api/v1/auth/logout",
        TokenBody(session.RefreshToken), rotated.AccessToken);
    Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

    using var refresh = await RefreshAsync(context, rotated.RefreshToken);
    Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    Assert.Equal(0, await CountActiveTokensAsync(context, session.User.Id));
  }

  // ── BUG-015: changing the password ends every other session ────────

  [Fact]
  public async Task ChangePassword_RevokesEveryOtherSession_AndReturnsANewOneForThisDevice()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var email = UniqueEmail("change");
    var first = await RegisterAsync(context, "change", email);
    var second = await LoginAsync(context, email, Password);

    using var change = await SendAsync(context, HttpMethod.Post, "/api/v1/customer/profile/password",
        JsonContent.Create(new CustomerPasswordChangeRequest(Password, "Changed123!")), second.AccessToken);
    Assert.Equal(HttpStatusCode.OK, change.StatusCode);
    var renewed = await ReadSessionAsync(change);

    using (var firstRefresh = await RefreshAsync(context, first.RefreshToken))
    {
      Assert.Equal(HttpStatusCode.Unauthorized, firstRefresh.StatusCode);
    }

    using (var secondRefresh = await RefreshAsync(context, second.RefreshToken))
    {
      Assert.Equal(HttpStatusCode.Unauthorized, secondRefresh.StatusCode);
    }

    using (var renewedRefresh = await RefreshAsync(context, renewed.RefreshToken))
    {
      Assert.Equal(HttpStatusCode.OK, renewedRefresh.StatusCode);
    }

    using var oldPassword = await SendAsync(context, HttpMethod.Post, "/api/v1/auth/login",
        JsonContent.Create(new LoginRequest(email, Password)));
    Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
  }

  [Fact]
  public async Task ChangePassword_WithAWrongCurrentPassword_KeepsEverySession()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var session = await RegisterAsync(context, "wrong-current");

    using var change = await SendAsync(context, HttpMethod.Post, "/api/v1/customer/profile/password",
        JsonContent.Create(new CustomerPasswordChangeRequest("NotTheCurrent1!", "Changed123!")), session.AccessToken);
    Assert.Equal(HttpStatusCode.UnprocessableEntity, change.StatusCode);

    using var refresh = await RefreshAsync(context, session.RefreshToken);
    Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
  }

  // ── BUG-016: rotation is single use under concurrency ───────────────

  [Fact]
  public async Task SimultaneousRefreshes_WithTheSameToken_OnlyOneGetsANewSession()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    for (var round = 0; round < 4; round++)
    {
      context.UseClient($"198.51.100.{10 + round}");
      var session = await RegisterAsync(context, $"race-{round}");

      var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => RefreshAsync(context, session.RefreshToken)));
      try
      {
        var winners = responses.Where(response => response.StatusCode == HttpStatusCode.OK).ToArray();
        Assert.Single(winners);
        Assert.All(responses.Where(response => response.StatusCode != HttpStatusCode.OK),
            response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));

        var successor = await ReadSessionAsync(winners[0]);
        using var next = await RefreshAsync(context, successor.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal(1, await CountActiveTokensAsync(context, session.User.Id));
      }
      finally
      {
        foreach (var response in responses)
        {
          response.Dispose();
        }
      }
    }
  }

  [Fact]
  public async Task LogoutRacingARefreshOfTheSameSession_NeverLeavesItAlive()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    for (var round = 0; round < 8; round++)
    {
      context.UseClient($"198.51.100.{40 + round}");
      var session = await RegisterAsync(context, $"logout-race-{round}");

      var refreshTask = RefreshAsync(context, session.RefreshToken);
      var logoutTask = SendAsync(context, HttpMethod.Post, "/api/v1/auth/logout", TokenBody(session.RefreshToken), session.AccessToken);
      using var refresh = await refreshTask;
      using var logout = await logoutTask;

      Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
      Assert.Contains(refresh.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized });
      if (refresh.StatusCode == HttpStatusCode.OK)
      {
        var rotated = await ReadSessionAsync(refresh);
        using var reuse = await RefreshAsync(context, rotated.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
      }

      Assert.Equal(0, await CountActiveTokensAsync(context, session.User.Id));
    }
  }

  [Fact]
  public async Task PasswordChangeRacingARefreshOfAnotherSession_LeavesOnlyTheNewSession()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    for (var round = 0; round < 6; round++)
    {
      context.UseClient($"198.51.100.{60 + round}");
      var email = UniqueEmail($"change-race-{round}");
      var other = await RegisterAsync(context, $"change-race-{round}", email);
      var device = await LoginAsync(context, email, Password);

      var refreshTask = RefreshAsync(context, other.RefreshToken);
      var changeTask = SendAsync(context, HttpMethod.Post, "/api/v1/customer/profile/password",
          JsonContent.Create(new CustomerPasswordChangeRequest(Password, $"Changed{round}23!")), device.AccessToken);
      using var refresh = await refreshTask;
      using var change = await changeTask;

      Assert.Equal(HttpStatusCode.OK, change.StatusCode);
      var renewed = await ReadSessionAsync(change);
      if (refresh.StatusCode == HttpStatusCode.OK)
      {
        var rotated = await ReadSessionAsync(refresh);
        using var reuse = await RefreshAsync(context, rotated.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
      }
      else
      {
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
      }

      // Only the session handed to the device that changed the password survives.
      Assert.Equal(1, await CountActiveTokensAsync(context, other.User.Id));
      using var renewedRefresh = await RefreshAsync(context, renewed.RefreshToken);
      Assert.Equal(HttpStatusCode.OK, renewedRefresh.StatusCode);
    }
  }

  // ── BUG-001: the limiter keys by the client behind the proxies ──────

  [Fact]
  public async Task CredentialLimit_IsSharedByLoginAndRegister_AndForgedForwardedEntriesDoNotEvadeIt()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    const string client = "203.0.113.50";
    var statuses = new List<HttpStatusCode>();
    for (var attempt = 0; attempt < 6; attempt++)
    {
      // Each request forges a different leftmost entry and arrives through a different Cloudflare edge.
      var forwardedFor = $"10.9.{attempt}.1, 192.0.2.{attempt + 1}, {client}, 104.16.{attempt}.{attempt + 2}";
      var path = attempt % 2 == 0 ? "/api/v1/auth/login" : "/api/v1/auth/register";
      HttpContent body = attempt % 2 == 0
          ? JsonContent.Create(new LoginRequest(UniqueEmail("nobody"), "Wrong123!"))
          : JsonContent.Create(new RegisterRequest("X", "not-an-email", "short", null));
      using var response = await SendAsync(context, HttpMethod.Post, path, body, forwardedFor: forwardedFor);
      statuses.Add(response.StatusCode);

      if (attempt == 5)
      {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var retryAfter = int.Parse(response.Headers.GetValues("Retry-After").Single());
        Assert.InRange(retryAfter, 1, 60);
      }
    }

    Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(5));

    // Another client behind the same edges keeps its own quota.
    using var otherClient = await SendAsync(context, HttpMethod.Post, "/api/v1/auth/login",
        JsonContent.Create(new LoginRequest(UniqueEmail("nobody"), "Wrong123!")), forwardedFor: $"198.51.100.200, {CloudflareEdge}");
    Assert.Equal(HttpStatusCode.Unauthorized, otherClient.StatusCode);
  }

  [Fact]
  public async Task PasswordResetLimit_AppliesFromTheFourthRequest()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var statuses = new List<HttpStatusCode>();
    for (var attempt = 0; attempt < 4; attempt++)
    {
      using var response = await SendAsync(context, HttpMethod.Post, "/api/v1/auth/forgot-password",
          JsonContent.Create(new ForgotPasswordRequest(UniqueEmail("nobody"))),
          forwardedFor: $"192.0.2.{attempt + 20}, 203.0.113.77, 104.16.9.{attempt + 1}");
      statuses.Add(response.StatusCode);
    }

    Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.TooManyRequests], statuses);
  }

  // ── BUG-013: access tokens stop working when their session ends ─────

  [Fact]
  public async Task AfterLogout_TheAccessTokenOfThatSessionIsRejected()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var session = await RegisterAsync(context, "logout-access");
    using (var before = await SendAsync(context, HttpMethod.Get, "/api/v1/auth/me", null, session.AccessToken))
    {
      Assert.Equal(HttpStatusCode.OK, before.StatusCode);
    }

    using (var logout = await SendAsync(context, HttpMethod.Post, "/api/v1/auth/logout", TokenBody(session.RefreshToken), session.AccessToken))
    {
      Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
    }

    using var me = await SendAsync(context, HttpMethod.Get, "/api/v1/auth/me", null, session.AccessToken);
    using var appointments = await SendAsync(context, HttpMethod.Get, "/api/v1/customer/appointments", null, session.AccessToken);
    Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, appointments.StatusCode);
  }

  [Fact]
  public async Task AfterAPasswordChange_OtherSessionsAccessTokensAreRejected_AndTheNewOneWorks()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var email = UniqueEmail("change-access");
    var other = await RegisterAsync(context, "change-access", email);
    var device = await LoginAsync(context, email, Password);

    using var change = await SendAsync(context, HttpMethod.Post, "/api/v1/customer/profile/password",
        JsonContent.Create(new CustomerPasswordChangeRequest(Password, "Changed123!")), device.AccessToken);
    var renewed = await ReadSessionAsync(change);

    using var otherMe = await SendAsync(context, HttpMethod.Get, "/api/v1/auth/me", null, other.AccessToken);
    using var deviceOldMe = await SendAsync(context, HttpMethod.Get, "/api/v1/auth/me", null, device.AccessToken);
    using var renewedMe = await SendAsync(context, HttpMethod.Get, "/api/v1/auth/me", null, renewed.AccessToken);
    Assert.Equal(HttpStatusCode.Unauthorized, otherMe.StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, deviceOldMe.StatusCode);
    Assert.Equal(HttpStatusCode.OK, renewedMe.StatusCode);
  }

  [Fact]
  public async Task ARefresh_KeepsTheSessionsEarlierAccessTokenValid_SoOtherTabsDoNotLoopRefreshing()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var session = await RegisterAsync(context, "rotation-access");
    using var refresh = await RefreshAsync(context, session.RefreshToken);
    var rotated = await ReadSessionAsync(refresh);

    using var earlier = await SendAsync(context, HttpMethod.Get, "/api/v1/auth/me", null, session.AccessToken);
    using var current = await SendAsync(context, HttpMethod.Get, "/api/v1/auth/me", null, rotated.AccessToken);
    Assert.Equal(HttpStatusCode.OK, earlier.StatusCode);
    Assert.Equal(HttpStatusCode.OK, current.StatusCode);

    var sessionClaim = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(session.AccessToken)
        .Claims.Single(claim => claim.Type == "sid").Value;
    var rotatedClaim = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(rotated.AccessToken)
        .Claims.Single(claim => claim.Type == "sid").Value;
    Assert.Equal(sessionClaim, rotatedClaim);
  }

  [Fact]
  public async Task LoggingOutOneSession_LeavesTheOtherSessionsOfTheAccountWorking()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var email = UniqueEmail("two-sessions");
    var first = await RegisterAsync(context, "two-sessions", email);
    var second = await LoginAsync(context, email, Password);

    using (var logout = await SendAsync(context, HttpMethod.Post, "/api/v1/auth/logout", TokenBody(first.RefreshToken), first.AccessToken))
    {
      Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
    }

    using var secondMe = await SendAsync(context, HttpMethod.Get, "/api/v1/auth/me", null, second.AccessToken);
    Assert.Equal(HttpStatusCode.OK, secondMe.StatusCode);
  }

  [Fact]
  public async Task AnAccessTokenWithoutASession_IsRejected()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var session = await RegisterAsync(context, "no-sid");
    var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
    var original = handler.ReadJwtToken(session.AccessToken);
    var claims = original.Claims.Where(claim => claim.Type is not ("sid" or "aud" or "iss" or "exp" or "nbf" or "iat")).ToList();
    var credentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
        new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(Encoding.UTF8.GetBytes("12345678901234567890123456789012-auth-tests-key")),
        Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);
    var forged = handler.WriteToken(new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
        "Barbershop.Tests", "Barbershop.Tests.Client", claims, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(30), credentials));

    using var me = await SendAsync(context, HttpMethod.Get, "/api/v1/auth/me", null, forged);
    Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
  }

  [Fact]
  public async Task ADeactivatedAccount_LosesAccessImmediately()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var session = await RegisterAsync(context, "deactivated");
    using (var scope = context.Factory.Services.CreateScope())
    {
      var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var user = await dbContext.Users.SingleAsync(candidate => candidate.Id == session.User.Id);
      user.Deactivate(DateTime.UtcNow);
      await dbContext.SaveChangesAsync();
    }

    using var me = await SendAsync(context, HttpMethod.Get, "/api/v1/auth/me", null, session.AccessToken);
    Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private async Task<SessionContext?> CreateContextAsync()
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return null;
    }

    var factory = new IntegrationTestFactory(_postgres.ConnectionString);
    await factory.ResetDatabaseAsync();
    var context = new SessionContext(factory);
    context.UseClient($"198.51.100.{Random.Shared.Next(100, 199)}");
    return context;
  }

  private static async Task<Session> RegisterAsync(SessionContext context, string label, string? email = null)
  {
    using var response = await SendAsync(context, HttpMethod.Post, "/api/v1/auth/register",
        JsonContent.Create(new RegisterRequest($"Person {label}", email ?? UniqueEmail(label), Password, null)));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    return await ReadSessionAsync(response);
  }

  private static async Task<Session> LoginAsync(SessionContext context, string email, string password)
  {
    using var response = await SendAsync(context, HttpMethod.Post, "/api/v1/auth/login",
        JsonContent.Create(new LoginRequest(email, password)));
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return await ReadSessionAsync(response);
  }

  private static Task<HttpResponseMessage> RefreshAsync(SessionContext context, string refreshToken)
      => SendAsync(context, HttpMethod.Post, "/api/v1/auth/refresh", TokenBody(refreshToken));

  private static async Task<HttpResponseMessage> SendAsync(
      SessionContext context,
      HttpMethod method,
      string path,
      HttpContent? content,
      string? accessToken = null,
      string? forwardedFor = null)
  {
    using var request = new HttpRequestMessage(method, path) { Content = content };
    request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor ?? context.ForwardedFor);
    if (accessToken is not null)
    {
      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    return await context.Client.SendAsync(request);
  }

  private static async Task<Session> ReadSessionAsync(HttpResponseMessage response)
  {
    var session = await response.Content.ReadFromJsonAsync<Session>();
    Assert.NotNull(session);
    Assert.False(string.IsNullOrWhiteSpace(session!.RefreshToken));
    return session;
  }

  private static async Task<int> CountActiveTokensAsync(SessionContext context, Guid userId)
  {
    using var scope = context.Factory.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var now = DateTime.UtcNow;
    return await dbContext.RefreshTokens.CountAsync(token => token.UserId == userId && token.RevokedAt == null && token.ExpiresAt > now);
  }

  private static StringContent Json(string raw) => new(raw, Encoding.UTF8, "application/json");

  private static HttpContent TokenBody(string refreshToken) => JsonContent.Create(new { refreshToken });

  private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

  private sealed record Session(string AccessToken, string RefreshToken, AuthUserResponse User);

  private sealed class SessionContext : IDisposable
  {
    public SessionContext(IntegrationTestFactory factory)
    {
      Factory = factory;
      // No cookie jar: tokens only travel in the body, as in the iOS standalone PWA.
      Client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
    }

    public IntegrationTestFactory Factory { get; }

    public HttpClient Client { get; }

    /// <summary>The client as Cloudflare saw it, followed by the edge hop.</summary>
    public string ForwardedFor { get; private set; } = string.Empty;

    public void UseClient(string clientIp) => ForwardedFor = $"{clientIp}, {CloudflareEdge}";

    public void Dispose()
    {
      Client.Dispose();
      Factory.Dispose();
    }
  }
}
