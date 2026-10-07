namespace Barbershop.Application.Notifications;

public interface IPushDiagnosticsService
{
    /// <summary>Server-side push configuration the browser needs (single source of truth for the VAPID public key).</summary>
    PushClientConfigView GetClientConfig();

    /// <summary>Sends a test notification to every device of the current user and reports the result per device.</summary>
    Task<PushTestResultView> SendTestAsync(Guid currentUserId, CancellationToken cancellationToken = default);
}
