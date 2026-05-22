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

public sealed record LogoutRequest(string? RefreshToken);