namespace Barbershop.Infrastructure.Configuration;

public sealed class LoyaltyExpirationWorkerOptions
{
    public const string SectionName = "LoyaltyExpirationWorker";

    /// <summary>Runs the job that records expired loyalty lots. Disabled in integration tests unless a test enables it.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Wait after startup before the first run.</summary>
    public int StartupDelaySeconds { get; init; } = 60;

    /// <summary>Time between runs (6 hours); the job is idempotent, so running more often is harmless.</summary>
    public int IntervalSeconds { get; init; } = 21600;
}
