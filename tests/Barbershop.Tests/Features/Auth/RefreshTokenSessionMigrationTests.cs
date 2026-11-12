using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Barbershop.Application.Auth;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.IdentityModel.Tokens;

namespace Barbershop.Tests.Features.Auth;

/// <summary>
/// AddRefreshTokenSessionId (BUG-013) over a database that already has sessions, including rotation chains created
/// before the deploy. Every token of a chain A -> B must end up in the same session, so a later logout presenting the
/// stale A still ends the session B continued; independent sessions of the account stay separate. Runs against a
/// disposable PostgreSQL only.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "RefreshTokenSessionMigration")]
public sealed class RefreshTokenSessionMigrationTests
{
  private const string MigrationBeforeSessions = "20261106235629_AddProductCatalog";
  private const string SigningKey = "12345678901234567890123456789012-auth-tests-key";

  private readonly PostgresContainerFixture _postgres;

  public RefreshTokenSessionMigrationTests(PostgresContainerFixture postgres) => _postgres = postgres;

  [Fact]
  public async Task ChainsKeepOneSession_IndependentSessionsStaySeparate_AndTheRollbackDropsOnlyTheColumn()
  {
    if (!_postgres.IsAvailable) return; // Only with PostgresContainerFixture.AllowSkipVariable set on purpose.

    await using var db = CreateContext();
    var seed = await SeedBeforeSessionsAsync(db);

    await db.GetService<IMigrator>().MigrateAsync();

    var tokens = await db.RefreshTokens.AsNoTracking().ToDictionaryAsync(token => token.Id);
    Assert.Equal(3, tokens.Count);
    // The chain A -> B is one session, identified by its first token.
    Assert.Equal(seed.A, tokens[seed.A].SessionId);
    Assert.Equal(seed.A, tokens[seed.B].SessionId);
    // The independent session C is its own.
    Assert.Equal(seed.C, tokens[seed.C].SessionId);

    await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeSessions);
    var columns = await db.Database
        .SqlQueryRaw<string>("SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'refresh_tokens'")
        .ToListAsync();
    Assert.DoesNotContain("SessionId", columns);
    Assert.Equal(3, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM public.refresh_tokens").SingleAsync());

    await db.GetService<IMigrator>().MigrateAsync();
  }

  [Fact]
  public async Task AChainFromBeforeTheDeploy_ContinuesAfterIt_AndALogoutWithItsStaleTokenEndsOnlyThatSession()
  {
    if (!_postgres.IsAvailable) return; // Only with PostgresContainerFixture.AllowSkipVariable set on purpose.

    // 1. Before the migration: chain A -> B (A revoked and replaced by B, B active) and an independent session C.
    await using (var db = CreateContext())
    {
      await SeedBeforeSessionsAsync(db);

      // 2. Apply the migration.
      await db.GetService<IMigrator>().MigrateAsync();
    }

    // The API on that same database, without resetting it.
    using var factory = new IntegrationTestFactory(_postgres.ConnectionString);
    using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    // 3. Renew B: the session continues.
    var renewedB = await RefreshAsync(client, RawB);
    using (var me = await GetMeAsync(client, renewedB.AccessToken))
    {
      Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    // Front-end recovery: an access token issued before the deploy has no "sid" and is rejected; the app then
    // renews with the refresh token it already had (C, also from before the migration) and goes on.
    using (var oldAccess = await GetMeAsync(client, CreateAccessTokenWithoutSession(SeedUserId)))
    {
      Assert.Equal(HttpStatusCode.Unauthorized, oldAccess.StatusCode);
    }

    var renewedC = await RefreshAsync(client, RawC);
    using (var meC = await GetMeAsync(client, renewedC.AccessToken))
    {
      Assert.Equal(HttpStatusCode.OK, meC.StatusCode);
    }

    // 4. Logout presenting the stale A, authenticated as the same account (through its other session C).
    using (var logout = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/logout",
               JsonContent.Create(new { refreshToken = RawA }), renewedC.AccessToken))
    {
      Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
    }

    // 5. The session B continued is over, access tokens included...
    using (var refreshB = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/refresh",
               JsonContent.Create(new { refreshToken = renewedB.RefreshToken })))
    {
      Assert.Equal(HttpStatusCode.Unauthorized, refreshB.StatusCode);
    }

    using (var meB = await GetMeAsync(client, renewedB.AccessToken))
    {
      Assert.Equal(HttpStatusCode.Unauthorized, meB.StatusCode);
    }

    // ...and the independent session C keeps working.
    using (var meCAfter = await GetMeAsync(client, renewedC.AccessToken))
    {
      Assert.Equal(HttpStatusCode.OK, meCAfter.StatusCode);
    }

    var renewedCAgain = await RefreshAsync(client, renewedC.RefreshToken);
    Assert.False(string.IsNullOrWhiteSpace(renewedCAgain.AccessToken));
  }

  // ── Seed before the migration ────────────────────────────────────────

  private static readonly Guid SeedUserId = Guid.Parse("6d1f5c2a-0000-4000-8000-000000000001");
  private const string RawA = "pre-migration-token-A-0123456789abcdef0123456789abcdef";
  private const string RawB = "pre-migration-token-B-0123456789abcdef0123456789abcdef";
  private const string RawC = "pre-migration-token-C-0123456789abcdef0123456789abcdef";

  private sealed record SeedIds(Guid A, Guid B, Guid C);

  private static async Task<SeedIds> SeedBeforeSessionsAsync(AppDbContext db)
  {
    await db.Database.EnsureDeletedAsync();
    await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeSessions);

    var a = Guid.NewGuid();
    var b = Guid.NewGuid();
    var c = Guid.NewGuid();
    var now = DateTime.UtcNow;

    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.users ("Id", "FullName", "Email", "NormalizedEmail", "PasswordHash", "IsActive", "CreatedAt", "FailedLoginCount")
        VALUES ({SeedUserId}, 'Persona', 'persona@example.com', 'PERSONA@EXAMPLE.COM', 'hash', true, {now}, 0)
        """);
    // Inserted active first, so A can point to B.
    await InsertTokenAsync(db, b, RawB, now, revokedAt: null, replacedBy: null);
    await InsertTokenAsync(db, a, RawA, now.AddMinutes(-30), revokedAt: now, replacedBy: b);
    await InsertTokenAsync(db, c, RawC, now, revokedAt: null, replacedBy: null);
    return new SeedIds(a, b, c);
  }

  private static Task<int> InsertTokenAsync(AppDbContext db, Guid id, string raw, DateTime createdAt, DateTime? revokedAt, Guid? replacedBy)
      => db.Database.ExecuteSqlInterpolatedAsync($"""
          INSERT INTO public.refresh_tokens ("Id", "UserId", "TokenHash", "ExpiresAt", "CreatedAt", "RevokedAt", "ReplacedByRefreshTokenId")
          VALUES ({id}, {SeedUserId}, {Hash(raw)}, {createdAt.AddDays(7)}, {createdAt}, {revokedAt}, {replacedBy})
          """);

  /// <summary>Same hashing as AuthService.ComputeTokenHash.</summary>
  private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw.Trim())));

  /// <summary>An access token as issued before sessions were tracked: valid signature, no "sid".</summary>
  private static string CreateAccessTokenWithoutSession(Guid userId)
  {
    var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256);
    var token = new JwtSecurityToken(
        "Barbershop.Tests",
        "Barbershop.Tests.Client",
        [new System.Security.Claims.Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
         new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, userId.ToString())],
        DateTime.UtcNow,
        DateTime.UtcNow.AddMinutes(30),
        credentials);
    return new JwtSecurityTokenHandler().WriteToken(token);
  }

  // ── HTTP helpers ─────────────────────────────────────────────────────

  private sealed record Session(string AccessToken, string RefreshToken, AuthUserResponse User);

  private static async Task<Session> RefreshAsync(HttpClient client, string refreshToken)
  {
    using var response = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/refresh", JsonContent.Create(new { refreshToken }));
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var session = await response.Content.ReadFromJsonAsync<Session>();
    Assert.NotNull(session);
    return session!;
  }

  private static Task<HttpResponseMessage> GetMeAsync(HttpClient client, string accessToken)
      => SendAsync(client, HttpMethod.Get, "/api/v1/auth/me", null, accessToken);

  private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, HttpContent? content, string? accessToken = null)
  {
    using var request = new HttpRequestMessage(method, path) { Content = content };
    request.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.250, 104.16.0.10");
    if (accessToken is not null)
    {
      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    return await client.SendAsync(request);
  }

  private AppDbContext CreateContext()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(_postgres.ConnectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
        .Options;
    return new AppDbContext(options);
  }
}
