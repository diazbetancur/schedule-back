namespace Barbershop.Infrastructure.Configuration;

public sealed class CorsOptions
{
    public const string SectionName = "Cors";
    public const string PolicyName = "BarbershopCors";

    public string[] AllowedOrigins { get; init; } = [];
    public bool AllowWildcardOriginInDevelopment { get; init; }
    public bool AllowCredentials { get; init; } = true;
}
