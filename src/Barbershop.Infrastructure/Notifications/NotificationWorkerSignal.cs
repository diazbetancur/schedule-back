namespace Barbershop.Infrastructure.Notifications;

/// <summary>Wakes the background worker as soon as something is queued, instead of waiting for the next poll.</summary>
internal sealed class NotificationWorkerSignal
{
  private readonly SemaphoreSlim _signal = new(0, 1);

  public void Notify()
  {
    try
    {
      _signal.Release();
    }
    catch (SemaphoreFullException)
    {
      // Already signalled; the worker will pick everything up in one pass.
    }
  }

  public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
      => _signal.WaitAsync(timeout, cancellationToken);
}
