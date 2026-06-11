namespace Barbershop.Infrastructure.Configuration;

public sealed class WebPushOptions
{
    public const string SectionName = "WebPush";

    public bool Enabled { get; init; }

    public string PublicKey { get; init; } = string.Empty;

    public string PrivateKey { get; init; } = string.Empty;

    public string ContactEmail { get; init; } = "support@example.com";
}
