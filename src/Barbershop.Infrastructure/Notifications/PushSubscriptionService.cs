using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Notifications;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Barbershop.Infrastructure.Notifications;

internal sealed class PushSubscriptionService : IPushSubscriptionService
{
  internal const int MaxUserAgentLength = 256;

  private readonly AppDbContext _dbContext;
  private readonly TimeProvider _timeProvider;

  public PushSubscriptionService(AppDbContext dbContext, TimeProvider timeProvider)
  {
    _dbContext = dbContext;
    _timeProvider = timeProvider;
  }

  /// <summary>
  /// Idempotent upsert. The browser endpoint identifies the device: if it already exists it is re-bound
  /// to the signed-in user and its keys refreshed. This is what moves a shared device from one user to
  /// the next, and what heals rotated keys.
  /// </summary>
  public async Task SubscribeAsync(Guid currentUserId, PushSubscriptionRequest request, CancellationToken cancellationToken = default)
  {
    ValidateRequest(request);

    var endpoint = request.Endpoint.Trim();
    var userAgent = TruncateUserAgent(request.UserAgent);

    var existing = await _dbContext.PushSubscriptions
        .SingleOrDefaultAsync(subscription => subscription.Endpoint == endpoint, cancellationToken);

    if (existing is not null)
    {
      existing.Refresh(currentUserId, request.P256dhKey, request.AuthKey, userAgent);
      await _dbContext.SaveChangesAsync(cancellationToken);
      return;
    }

    var subscription = new PushSubscription(
        currentUserId,
        endpoint,
        request.P256dhKey,
        request.AuthKey,
        _timeProvider.GetUtcNow().UtcDateTime,
        userAgent);

    _dbContext.PushSubscriptions.Add(subscription);

    try
    {
      await _dbContext.SaveChangesAsync(cancellationToken);
    }
    catch (DbUpdateException exception) when (IsUniqueViolation(exception))
    {
      // Two requests registered the same endpoint at once (e.g. silent resync + manual enable).
      _dbContext.Entry(subscription).State = EntityState.Detached;

      var winner = await _dbContext.PushSubscriptions
          .SingleAsync(candidate => candidate.Endpoint == endpoint, cancellationToken);

      winner.Refresh(currentUserId, request.P256dhKey, request.AuthKey, userAgent);
      await _dbContext.SaveChangesAsync(cancellationToken);
    }
  }

  public async Task UnsubscribeAsync(Guid currentUserId, PushUnsubscribeRequest request, CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace(request.Endpoint))
    {
      return;
    }

    var endpoint = request.Endpoint.Trim();

    var subscription = await _dbContext.PushSubscriptions
        .SingleOrDefaultAsync(
            candidate => candidate.Endpoint == endpoint && candidate.UserId == currentUserId,
            cancellationToken);

    if (subscription is null)
    {
      return;
    }

    _dbContext.PushSubscriptions.Remove(subscription);
    await _dbContext.SaveChangesAsync(cancellationToken);
  }

  internal static string? TruncateUserAgent(string? userAgent)
  {
    if (string.IsNullOrWhiteSpace(userAgent))
    {
      return null;
    }

    var trimmed = userAgent.Trim();
    return trimmed.Length <= MaxUserAgentLength ? trimmed : trimmed[..MaxUserAgentLength];
  }

  private static void ValidateRequest(PushSubscriptionRequest request)
  {
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    if (string.IsNullOrWhiteSpace(request.Endpoint)
        || !Uri.TryCreate(request.Endpoint.Trim(), UriKind.Absolute, out var endpointUri)
        || endpointUri.Scheme != Uri.UriSchemeHttps)
    {
      errors["endpoint"] = ["Endpoint must be an absolute HTTPS URL."];
    }

    if (string.IsNullOrWhiteSpace(request.P256dhKey))
    {
      errors["p256dhKey"] = ["P256dhKey is required."];
    }

    if (string.IsNullOrWhiteSpace(request.AuthKey))
    {
      errors["authKey"] = ["AuthKey is required."];
    }

    if (errors.Count > 0)
    {
      throw new ValidationProblemException(errors);
    }
  }

  private static bool IsUniqueViolation(DbUpdateException exception)
      => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
