using System.ComponentModel.DataAnnotations;

namespace Barbershop.Infrastructure.Configuration;

public sealed class AppOptions
{
    public const string SectionName = "App";

    [Required]
    public string Name { get; init; } = "Barbershop API";

    [Required]
    public string Version { get; init; } = "0.2.0";

    [Required]
    public string ApiBasePath { get; init; } = "/api/v1";

    [Required]
    public string CorrelationIdHeaderName { get; init; } = "X-Correlation-ID";

    public string FrontendUrl { get; init; } = string.Empty;
}
