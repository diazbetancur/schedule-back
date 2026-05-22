namespace Barbershop.Application.Auth;

public interface IAuthService
{
  Task<TokenResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);

  Task<TokenResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

  Task<TokenResponse> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default);

  Task LogoutAsync(Guid currentUserId, LogoutRequest request, CancellationToken cancellationToken = default);

  Task<AuthUserResponse> GetCurrentUserAsync(Guid currentUserId, CancellationToken cancellationToken = default);
}