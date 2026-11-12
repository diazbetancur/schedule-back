namespace Barbershop.Application.Auth;

public interface IAuthService
{
  Task<TokenResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);

  Task<TokenResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

  Task<TokenResponse> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default);

  Task LogoutAsync(Guid currentUserId, LogoutRequest request, CancellationToken cancellationToken = default);

  /// <summary>
  /// Opens a new session (refresh token + access token) for an active account that already proved who it is, e.g.
  /// right after changing its password revoked every previous session.
  /// </summary>
  Task<TokenResponse> IssueSessionAsync(Guid userId, CancellationToken cancellationToken = default);

  Task<AuthUserResponse> GetCurrentUserAsync(Guid currentUserId, CancellationToken cancellationToken = default);

  Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken = default);

  Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default);
}