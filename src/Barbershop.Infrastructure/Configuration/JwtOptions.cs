using System.ComponentModel.DataAnnotations;

namespace Barbershop.Infrastructure.Configuration;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public bool Enabled { get; init; }

    [Required]
    public string Issuer { get; init; } = string.Empty;

    [Required]
    public string Audience { get; init; } = string.Empty;

    [Required]
    public string SigningKey { get; init; } = string.Empty;

    [Range(1, 1440)]
    public int AccessTokenMinutes { get; init; } = 60;

    [Range(1, 90)]
    public int RefreshTokenDays { get; init; } = 7;

    public bool RequireHttpsMetadata { get; init; } = true;

    [Range(5, 1440)]
    public int PasswordResetTokenMinutes { get; init; } = 30;
}
