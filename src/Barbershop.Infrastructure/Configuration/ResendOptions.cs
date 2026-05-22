using System.ComponentModel.DataAnnotations;

namespace Barbershop.Infrastructure.Configuration;

public sealed class ResendOptions
{
    public const string SectionName = "Resend";

    public string ApiKey { get; init; } = string.Empty;

    [Url]
    public string ApiBaseUrl { get; init; } = "https://api.resend.com";

    public bool SandboxMode { get; init; } = true;
}
