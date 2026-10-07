using Barbershop.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Barbershop.Infrastructure.Notifications;

/// <summary>
/// Sends queued push notifications (with retries), creates appointment reminders every minute and
/// cleans up old rows. Everything lives in PostgreSQL, so a restart or a sleeping instance loses nothing:
/// pending work is picked up on the next run.
/// </summary>
internal sealed class NotificationBackgroundWorker : BackgroundService
{
  private const int BatchSize = 25;
  private const int MaxBatchesPerIteration = 20;
  private static readonly TimeSpan ReminderInterval = TimeSpan.FromMinutes(1);
  private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);

  private readonly IServiceScopeFactory _scopeFactory;
  private readonly NotificationWorkerSignal _signal;
  private readonly TimeProvider _timeProvider;
  private readonly NotificationWorkerOptions _options;
  private readonly ILogger<NotificationBackgroundWorker> _logger;

  public NotificationBackgroundWorker(
      IServiceScopeFactory scopeFactory,
      NotificationWorkerSignal signal,
      TimeProvider timeProvider,
      IOptions<NotificationWorkerOptions> options,
      ILogger<NotificationBackgroundWorker> logger)
  {
    _scopeFactory = scopeFactory;
    _signal = signal;
    _timeProvider = timeProvider;
    _options = options.Value;
    _logger = logger;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    if (!_options.Enabled)
    {
      _logger.LogInformation("Notification worker is disabled ({Section}:Enabled = false).", NotificationWorkerOptions.SectionName);
      return;
    }

    var pollInterval = TimeSpan.FromSeconds(Math.Clamp(_options.PollIntervalSeconds, 1, 300));
    var nextReminderRun = DateTime.MinValue;
    var nextCleanup = DateTime.MinValue;

    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        using var scope = _scopeFactory.CreateScope();
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        if (_options.RemindersEnabled && nowUtc >= nextReminderRun)
        {
          nextReminderRun = nowUtc + ReminderInterval;
          await scope.ServiceProvider.GetRequiredService<AppointmentReminderService>().RunAsync(stoppingToken);
        }

        var processor = scope.ServiceProvider.GetRequiredService<NotificationDeliveryProcessor>();
        var batches = 0;
        while (await processor.ProcessDueAsync(BatchSize, stoppingToken) == BatchSize && ++batches < MaxBatchesPerIteration)
        {
        }

        if (nowUtc >= nextCleanup)
        {
          nextCleanup = nowUtc + CleanupInterval;
          await processor.CleanupAsync(stoppingToken);
        }
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        break;
      }
      catch (Exception exception)
      {
        // Never let the worker die: log and try again on the next round.
        _logger.LogError(exception, "Notification worker iteration failed.");
      }

      try
      {
        await _signal.WaitAsync(pollInterval, stoppingToken);
      }
      catch (OperationCanceledException)
      {
        break;
      }
    }
  }
}
