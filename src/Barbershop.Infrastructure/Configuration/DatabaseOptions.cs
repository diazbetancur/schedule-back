using System.ComponentModel.DataAnnotations;

namespace Barbershop.Infrastructure.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required]
    public string ConnectionString { get; init; } = string.Empty;

    [Range(1, 600)]
    public int CommandTimeoutSeconds { get; init; } = 30;

    public string AdminDatabase { get; init; } = string.Empty;

    public bool EnableDetailedErrors { get; init; }
    public bool EnableSensitiveDataLogging { get; init; }
}
