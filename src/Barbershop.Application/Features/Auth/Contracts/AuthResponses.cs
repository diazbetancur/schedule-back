namespace Barbershop.Application.Auth;

public sealed record AuthUserResponse(
    Guid Id,
    string FullName,
    string Email,
    string? PhoneNumber,
    IReadOnlyList<string> Roles,
    Guid? ProfilePhotoId,
    string? ProfilePhotoUrl);

public sealed record TokenResponse(
    string AccessToken,
    int ExpiresInSeconds,
    string RefreshToken,
    AuthUserResponse User);