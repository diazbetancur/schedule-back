namespace Barbershop.Infrastructure.Configuration;

public sealed class NotificationWorkerOptions
{
    public const string SectionName = "NotificationWorker";

    /// <summary>Runs the background worker that sends queued push notifications. Disabled in integration tests.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Appointment reminders (customers 24 h / 2 h, staff hourly summary).</summary>
    public bool RemindersEnabled { get; init; } = true;

    /// <summary>Fallback poll interval; new notifications wake the worker immediately anyway.</summary>
    public int PollIntervalSeconds { get; init; } = 15;
}
