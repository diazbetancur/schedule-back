namespace Barbershop.Application.Auth;

public sealed record AuthUserResponse(
    Guid Id,
    string FullName,
    string Email,
    string? PhoneNumber,
    IReadOnlyList<string> Roles,
    Guid? ProfilePhotoId,
    string? ProfilePhotoUrl,
    /// <summary>Same codes as the token's permission claims, so the UI can show or hide actions; the API still authorizes from the token.</summary>
    IReadOnlyList<string> Permissions);

public sealed record TokenResponse(
    string AccessToken,
    int ExpiresInSeconds,
    string RefreshToken,
    AuthUserResponse User);