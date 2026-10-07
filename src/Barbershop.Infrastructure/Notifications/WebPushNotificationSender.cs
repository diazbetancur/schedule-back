using System.Net;
using Barbershop.Application.Notifications;
using Barbershop.Infrastructure.Configuration;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebPush;
using DomainPushSubscription = Barbershop.Domain.Users.PushSubscription;
using WebPushSubscription = WebPush.PushSubscription;

namespace Barbershop.Infrastructure.Notifications;

internal sealed class WebPushNotificationSender : IPushNotificationSender, IPushDiagnosticsService
{
  private const string NotificationIconPath = "/icons/icon-192x192.png";
  private const string NotificationBadgePath = "/icons/badge-96x96.png";
  private const int MaxErrorLength = 300;
  private const int MaxTimeToLiveSeconds = 2_419_200; // 28 days, the Web Push maximum most services accept.

  private static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromHours(24);
  private static readonly string[] PlaceholderDomains = ["localhost", "example.com", "example.org", "example.net"];
  private static int _configurationWarningsLogged;

  private readonly AppDbContext _dbContext;
  private readonly WebPushOptions _options;
  private readonly WebPushClient _client;
  private readonly ILogger<WebPushNotificationSender> _logger;

  public WebPushNotificationSender(
      AppDbContext dbContext,
      IOptions<WebPushOptions> options,
      WebPushClient client,
      ILogger<WebPushNotificationSender> logger)
  {
    _dbContext = dbContext;
    _options = options.Value;
    _client = client;
    _logger = logger;
  }

  public async Task SendToUsersAsync(IReadOnlyCollection<Guid> userIds, PushNotificationMessage message, CancellationToken cancellationToken = default)
  {
    if (!_options.Enabled || userIds.Count == 0)
    {
      return;
    }

    var subscriptions = await _dbContext.PushSubscriptions
        .Where(subscription => userIds.Contains(subscription.UserId))
        .ToListAsync(cancellationToken);

    if (subscriptions.Count == 0)
    {
      return;
    }

    await DeliverAsync(subscriptions, message, cancellationToken);
  }

  public PushClientConfigView GetClientConfig()
      => _options.Enabled && !string.IsNullOrWhiteSpace(_options.PublicKey)
          ? new PushClientConfigView(true, _options.PublicKey.Trim())
          : new PushClientConfigView(false, null);

  public async Task<PushTestResultView> SendTestAsync(Guid currentUserId, CancellationToken cancellationToken = default)
  {
    var warnings = GetConfigurationWarnings(_options).ToList();

    if (!_options.Enabled)
    {
      warnings.Add("Las notificaciones push están desactivadas en el servidor (WebPush:Enabled = false).");
      return new PushTestResultView(false, 0, 0, [], warnings);
    }

    var subscriptions = await _dbContext.PushSubscriptions
        .Where(subscription => subscription.UserId == currentUserId)
        .ToListAsync(cancellationToken);

    if (subscriptions.Count == 0)
    {
      warnings.Add("Tu cuenta no tiene dispositivos registrados. Activa las notificaciones en este dispositivo y vuelve a intentar.");
      return new PushTestResultView(true, 0, 0, [], warnings);
    }

    var message = new PushNotificationMessage(
        "Notificación de prueba",
        "Si ves esto, las notificaciones funcionan en este dispositivo.",
        "/account/notifications",
        "push-test",
        IsTimeSensitive: true,
        TimeToLive: TimeSpan.FromMinutes(10));

    var results = await DeliverAsync(subscriptions, message, cancellationToken);
    return new PushTestResultView(true, results.Count, results.Count(result => result.Delivered), results, warnings);
  }

  internal static string BuildSubject(string contact)
  {
    var value = (contact ?? string.Empty).Trim();

    return value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        ? value
        : $"mailto:{value}";
  }

  /// <summary>
  /// Apple's push service validates the VAPID "sub" claim and rejects placeholder domains (BadJwtToken),
  /// which silently breaks delivery to iPhones. Flag them so they show up in logs and in the test endpoint.
  /// </summary>
  internal static IReadOnlyList<string> GetConfigurationWarnings(WebPushOptions options)
  {
    if (!options.Enabled)
    {
      return [];
    }

    var subject = BuildSubject(options.ContactEmail);
    var host = subject.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
        ? subject[(subject.IndexOf('@') + 1)..]
        : Uri.TryCreate(subject, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    host = host.Trim().TrimEnd('>').ToLowerInvariant();

    var isMailtoWithoutAddress = subject.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) && !subject.Contains('@');

    var looksInvalid = string.IsNullOrWhiteSpace(host)
        || isMailtoWithoutAddress
        || host.EndsWith(".local", StringComparison.Ordinal)
        || PlaceholderDomains.Any(domain => host == domain || host.EndsWith($".{domain}", StringComparison.Ordinal));

    if (!looksInvalid)
    {
      return [];
    }

    return [$"WebPush:ContactEmail ('{options.ContactEmail}') no es un correo o URL real. Apple puede rechazar los envíos a iPhone (BadJwtToken). Configura WebPush__ContactEmail con un correo real del negocio."];
  }

  private async Task<IReadOnlyList<PushDeviceResultView>> DeliverAsync(
      IReadOnlyList<DomainPushSubscription> subscriptions,
      PushNotificationMessage message,
      CancellationToken cancellationToken)
  {
    LogConfigurationWarningsOnce();

    var vapidDetails = new VapidDetails(
        BuildSubject(_options.ContactEmail),
        _options.PublicKey.Trim(),
        _options.PrivateKey.Trim());

    var payload = WebPushPayloadBuilder.Build(message, NotificationIconPath, NotificationBadgePath);
    var timeToLive = (int)Math.Clamp((message.TimeToLive ?? DefaultTimeToLive).TotalSeconds, 0, MaxTimeToLiveSeconds);

    var sendOptions = new Dictionary<string, object>
    {
      ["vapidDetails"] = vapidDetails,
      ["TTL"] = timeToLive,
      ["headers"] = new Dictionary<string, object>
      {
        ["Urgency"] = message.IsTimeSensitive ? "high" : "normal",
      },
    };

    var results = new List<PushDeviceResultView>(subscriptions.Count);
    var staleSubscriptionIds = new List<Guid>();

    foreach (var subscription in subscriptions)
    {
      var target = new WebPushSubscription(subscription.Endpoint, subscription.P256dhKey, subscription.AuthKey);
      var pushService = DescribePushService(subscription.Endpoint);

      try
      {
        await _client.SendNotificationAsync(target, payload, sendOptions, cancellationToken);
        results.Add(new PushDeviceResultView(subscription.Id, pushService, subscription.UserAgent, true, null, null, false));
      }
      catch (WebPushException webPushException)
          when (webPushException.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
      {
        staleSubscriptionIds.Add(subscription.Id);
        results.Add(new PushDeviceResultView(
            subscription.Id,
            pushService,
            subscription.UserAgent,
            false,
            (int)webPushException.StatusCode,
            "La suscripción expiró en el navegador y se eliminó.",
            true));
      }
      catch (WebPushException webPushException)
      {
        var detail = await ReadErrorDetailAsync(webPushException);

        _logger.LogWarning(
            webPushException,
            "Push delivery failed for subscription {SubscriptionId} ({PushService}) with status {StatusCode}: {Detail}",
            subscription.Id,
            pushService,
            (int)webPushException.StatusCode,
            detail);

        results.Add(new PushDeviceResultView(
            subscription.Id,
            pushService,
            subscription.UserAgent,
            false,
            (int)webPushException.StatusCode,
            detail,
            false));
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        _logger.LogWarning(
            ex,
            "Unexpected error delivering push to subscription {SubscriptionId} ({PushService}).",
            subscription.Id,
            pushService);

        results.Add(new PushDeviceResultView(subscription.Id, pushService, subscription.UserAgent, false, null, Truncate(ex.Message), false));
      }
    }

    if (staleSubscriptionIds.Count > 0)
    {
      await _dbContext.PushSubscriptions
          .Where(subscription => staleSubscriptionIds.Contains(subscription.Id))
          .ExecuteDeleteAsync(cancellationToken);
    }

    return results;
  }

  private void LogConfigurationWarningsOnce()
  {
    if (Interlocked.Exchange(ref _configurationWarningsLogged, 1) == 1)
    {
      return;
    }

    foreach (var warning in GetConfigurationWarnings(_options))
    {
      _logger.LogWarning("WebPush configuration: {Warning}", warning);
    }
  }

  private static async Task<string> ReadErrorDetailAsync(WebPushException exception)
  {
    try
    {
      if (exception.HttpResponseMessage?.Content is { } content)
      {
        var body = await content.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(body))
        {
          return Truncate(body.Trim());
        }
      }
    }
    catch (Exception)
    {
      // Diagnostics only: fall back to the exception message.
    }

    return Truncate(exception.Message);
  }

  private static string DescribePushService(string endpoint)
  {
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
    {
      return "desconocido";
    }

    var host = uri.Host.ToLowerInvariant();

    return host switch
    {
      "fcm.googleapis.com" => "Google (Chrome / Android)",
      "web.push.apple.com" => "Apple (Safari / iPhone)",
      "updates.push.services.mozilla.com" => "Mozilla (Firefox)",
      _ when host.EndsWith(".notify.windows.com", StringComparison.Ordinal) => "Microsoft (Edge / Windows)",
      _ => host,
    };
  }

  private static string Truncate(string value)
      => value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];
}
