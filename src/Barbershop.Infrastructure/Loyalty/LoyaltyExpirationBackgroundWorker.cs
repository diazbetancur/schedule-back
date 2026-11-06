using Barbershop.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Barbershop.Infrastructure.Loyalty;

/// <summary>
/// Records the expiration of redemptions past their deadline (releasing their points) and of lots past their last
/// valid day. Queries never count expired points and approvals and deliveries recheck deadlines even before this
/// runs; it is idempotent and safe to run several times a day.
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
          // Redemptions past their deadline first: their release may expire points of lots that are past due too.
          var expiredRedemptions = await scope.ServiceProvider.GetRequiredService<LoyaltyRedemptionService>().ExpireDueRedemptionsAsync(stoppingToken);
          if (expiredRedemptions > 0)
          {
            _logger.LogInformation("Recorded the expiration of {Count} loyalty redemption(s).", expiredRedemptions);
          }

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
