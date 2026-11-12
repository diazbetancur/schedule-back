namespace Barbershop.Application.Auth;

/// <summary>
/// Checks, on every authenticated request, that the session an access token belongs to is still active (BUG-013):
/// signing out, changing or resetting the password, or deactivating the account stops its access tokens right away.
/// </summary>
public interface ISessionStateValidator
{
  Task<bool> IsActiveAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default);
}
