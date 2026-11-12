namespace Barbershop.Application.Auth;

public sealed record RegisterRequest(
    string FullName,
    string Email,
    string Password,
    string? PhoneNumber);

public sealed record LoginRequest(
    string Email,
    string Password);

public sealed record RefreshRequest(string RefreshToken);

/// <summary>
/// The refresh tokens a client presents when signing out: the cookie one and, for clients without cookies (iOS
/// standalone PWA), the one sent in the body. Only tokens of the signed-in account are revoked.
/// </summary>
public sealed record LogoutRequest(string? RefreshToken, string? AdditionalRefreshToken = null);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Token, string NewPassword);