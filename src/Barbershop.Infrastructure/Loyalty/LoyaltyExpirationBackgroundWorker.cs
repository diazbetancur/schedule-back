using Barbershop.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Barbershop.Infrastructure.Loyalty;

/// <summary>
/// Records the expiration of lots past their last valid day. Queries never count expired points even before this
/// runs, so it only writes history; it is idempotent and safe to run several times a day.
/// </summary>
internal sealed class LoyaltyExpirationBackgroundWorker : BackgroundService
{
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly LoyaltyExpirationWorkerOptions _options;
  private readonly ILogger<LoyaltyExpirationBackgroundWorker> _logger;

  public LoyaltyExpirationBackgroundWorker(
      IServiceScopeFactory scopeFactory,
      IOptions<LoyaltyExpirationWorkerOptions> options,
      ILogger<LoyaltyExpirationBackgroundWorker> logger)
  {
    _scopeFactory = scopeFactory;
    _options = options.Value;
    _logger = logger;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    if (!_options.Enabled)
    {
      _logger.LogInformation("Loyalty expiration worker is disabled ({Section}:Enabled = false).", LoyaltyExpirationWorkerOptions.SectionName);
      return;
    }

    var startupDelay = TimeSpan.FromSeconds(Math.Max(0, _options.StartupDelaySeconds));
    var interval = TimeSpan.FromSeconds(Math.Max(1, _options.IntervalSeconds));
    try
    {
      await Task.Delay(startupDelay, stoppingToken);
      while (!stoppingToken.IsCancellationRequested)
      {
        try
        {
          using var scope = _scopeFactory.CreateScope();
          var expired = await scope.ServiceProvider.GetRequiredService<LoyaltyWalletService>().ExpireDueLotsAsync(stoppingToken);
          if (expired > 0)
          {
            _logger.LogInformation("Recorded the expiration of {Count} loyalty lot(s).", expired);
          }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
          _logger.LogError(exception, "Loyalty expiration run failed; it will be retried on the next run.");
        }

        await Task.Delay(interval, stoppingToken);
      }
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
    }
  }
}
