using System.ComponentModel.DataAnnotations;

namespace Barbershop.Infrastructure.Configuration;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public bool Enabled { get; init; }

    [Required]
    public string Provider { get; init; } = "Resend";

    [EmailAddress]
    public string DefaultFromAddress { get; init; } = "noreply@example.com";

    [Required]
    public string DefaultFromName { get; init; } = "Barbershop";
}
