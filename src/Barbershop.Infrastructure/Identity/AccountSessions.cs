using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Identity;

/// <summary>
/// Serializes everything that creates, rotates or revokes an account's refresh tokens (login, refresh, logout,
/// password change and reset) on the account's row (<c>users</c>, <c>FOR UPDATE</c>), the same per-account lock
/// <c>CustomerManagementService.LockAccountAsync</c> takes.
/// </summary>
/// <remarks>
/// Under READ COMMITTED a bulk "revoke every active token" statement only sees the tokens committed when it starts,
/// so a refresh committing a rotated token at the same time would survive a password change or a logout. Holding the
/// account lock until commit makes these operations run one after the other: whatever runs second sees what the first
/// one wrote. Outside PostgreSQL (in-memory unit tests) the lock and the transaction are skipped.
/// </remarks>
internal static class AccountSessions
{
    /// <summary>How far a logout follows a rotation chain; a real chain is one or two links long.</summary>
    private const int MaxRotationChainLength = 32;

    public static async Task<T> InAccountTransactionAsync<T>(
        AppDbContext dbContext,
        Func<Task<T>> work,
        CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational() || dbContext.Database.CurrentTransaction is not null)
        {
            return await work();
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var result = await work();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public static async Task InAccountTransactionAsync(
        AppDbContext dbContext,
        Func<Task> work,
        CancellationToken cancellationToken)
    {
        await InAccountTransactionAsync<object?>(dbContext, async () =>
        {
            await work();
            return null;
        }, cancellationToken);
    }

    /// <summary>Takes the account lock; held until the surrounding transaction ends. A no-op outside PostgreSQL.</summary>
    public static async Task LockAccountAsync(AppDbContext dbContext, Guid userId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsNpgsql())
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM public.users WHERE \"Id\" = {userId} FOR UPDATE", cancellationToken);
        }
    }

    /// <summary>
    /// Marks every active refresh token of the account as revoked (tracked; the caller saves). Must run after
    /// <see cref="LockAccountAsync"/> so it also sees a token another request committed just before.
    /// </summary>
    public static async Task<int> RevokeAllRefreshTokensAsync(
        AppDbContext dbContext,
        Guid userId,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var activeTokens = await dbContext.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var token in activeTokens)
        {
            token.Revoke(utcNow);
        }

        return activeTokens.Count;
    }

    /// <summary>
    /// Ends the session the given token belongs to: every token of that session is revoked, so a logout racing a
    /// refresh of the same session (or presenting a stale token of it) cannot leave the refreshed session alive, and
    /// its access tokens stop being accepted (BUG-002, BUG-013). Tokens of other accounts are never touched. Tracked;
    /// the caller saves.
    /// </summary>
    public static async Task RevokeSessionAsync(
        AppDbContext dbContext,
        Guid userId,
        string tokenHash,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var presented = await dbContext.RefreshTokens
            .Where(candidate => candidate.UserId == userId && candidate.TokenHash == tokenHash)
            .Select(candidate => new { candidate.SessionId, candidate.ReplacedByRefreshTokenId })
            .SingleOrDefaultAsync(cancellationToken);

        if (presented is null)
        {
            return;
        }

        // The presented token's session plus, defensively, the sessions of the tokens it was rotated into: a chain
        // that crossed a session boundary (data from before sessions existed) still ends at its active token. Only
        // sessions reached through this token's own chain are touched, never the account's other sessions.
        var sessionIds = new HashSet<Guid> { presented.SessionId };
        var nextId = presented.ReplacedByRefreshTokenId;
        for (var link = 0; nextId is { } id && link < MaxRotationChainLength; link++)
        {
            var successor = await dbContext.RefreshTokens
                .Where(candidate => candidate.Id == id && candidate.UserId == userId)
                .Select(candidate => new { candidate.SessionId, candidate.ReplacedByRefreshTokenId })
                .SingleOrDefaultAsync(cancellationToken);
            if (successor is null)
            {
                break;
            }

            sessionIds.Add(successor.SessionId);
            nextId = successor.ReplacedByRefreshTokenId;
        }

        var activeTokens = await dbContext.RefreshTokens
            .Where(candidate => candidate.UserId == userId && sessionIds.Contains(candidate.SessionId) && candidate.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var token in activeTokens)
        {
            token.Revoke(utcNow);
        }
    }

    /// <summary>
    /// Whether an access token of this session may still be used: the session has an active refresh token and the
    /// account is active. One indexed query per authenticated request.
    /// </summary>
    public static Task<bool> IsSessionActiveAsync(
        AppDbContext dbContext,
        Guid userId,
        Guid sessionId,
        DateTime utcNow,
        CancellationToken cancellationToken)
        => dbContext.RefreshTokens
            .AsNoTracking()
            .AnyAsync(token => token.SessionId == sessionId
                && token.UserId == userId
                && token.RevokedAt == null
                && token.ExpiresAt > utcNow
                && token.User.IsActive, cancellationToken);

    public static Task ReloadAsync(AppDbContext dbContext, object entity, CancellationToken cancellationToken)
        => dbContext.Database.IsRelational()
            ? dbContext.Entry(entity).ReloadAsync(cancellationToken)
            : Task.CompletedTask;

    public static bool IsUsable(RefreshToken token, DateTime utcNow)
        => token.ReplacedByRefreshTokenId is null && token.IsActive(utcNow);
}
