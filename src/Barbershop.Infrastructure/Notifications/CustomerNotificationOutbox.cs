using System.Globalization;
using Barbershop.Application.Notifications;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Notifications;

/// <summary>
/// ADR-0019 N2: queues a notification for the account linked to a customer profile inside the caller's unit of work.
/// It only adds the inbox entry and its push delivery to the context: the caller's SaveChanges (and transaction) stores
/// them together with the business change, so a failed or rolled back operation never notifies and a retried one that
/// the operation itself treats as a replay never notifies twice. The notification worker sends the push on its next
/// round (it polls the queue). A profile without an account gets nothing.
/// </summary>
internal sealed class CustomerNotificationOutbox
{
  private const int MaxTitleLength = 160;
  private const int MaxBodyLength = 1000;
  private static readonly TimeSpan TimeToLive = TimeSpan.FromHours(24);

  internal static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("es-CO");

  private readonly AppDbContext _dbContext;
  private readonly TimeProvider _timeProvider;

  public CustomerNotificationOutbox(AppDbContext dbContext, TimeProvider timeProvider)
  {
    _dbContext = dbContext;
    _timeProvider = timeProvider;
  }

  public async Task QueueForCustomerAsync(
      Guid customerId, string type, PushNotificationMessage message, CancellationToken cancellationToken)
  {
    var userId = await _dbContext.Customers.AsNoTracking()
        .Where(profile => profile.Id == customerId)
        .Select(profile => profile.UserId)
        .SingleOrDefaultAsync(cancellationToken);
    if (userId is { } accountId)
    {
      Queue(accountId, type, message);
    }
  }

  public void Queue(Guid userId, string type, PushNotificationMessage message)
  {
    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
    var title = Truncate(message.Title, MaxTitleLength);
    var body = Truncate(message.Body, MaxBodyLength);
    var url = WebPushPayloadBuilder.NormalizeUrl(message.Url);

    var inboxEntry = new UserNotification(userId, type, title, body, url, nowUtc);
    _dbContext.UserNotifications.Add(inboxEntry);
    _dbContext.NotificationDeliveries.Add(new NotificationDelivery(
        userId,
        inboxEntry.Id,
        NotificationDeliveryChannel.Push,
        title,
        body,
        url,
        message.Tag,
        message.IsTimeSensitive,
        nowUtc,
        nowUtc + (message.TimeToLive ?? TimeToLive)));
  }

  internal static string Points(int points) => points == 1 ? "1 punto" : $"{points} puntos";

  internal static string Date(DateOnly date) => date.ToString("d 'de' MMMM", DisplayCulture);

  private static string Truncate(string value, int maxLength)
      => value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
}

/// <summary>ADR-0019 N1: texts and links of the membership and points notifications (Spanish, tuteo).</summary>
internal static class CustomerNotificationMessages
{
  public const string LoyaltyUrl = "/customer/loyalty";
  public const string MembershipsUrl = "/customer/memberships";

  public static string MembershipUrl(Guid membershipId) => $"{MembershipsUrl}/{membershipId}";

  public static PushNotificationMessage MembershipSold(Guid membershipId, string planName)
      => new(
          "Registramos tu membresía",
          $"Tu membresía «{planName}» quedó registrada. Te avisaremos cuando esté activa.",
          MembershipUrl(membershipId),
          $"membership-{membershipId:N}",
          IsTimeSensitive: false);

  public static PushNotificationMessage MembershipActivated(Guid membershipId, string planName, DateOnly endsOn, bool justSold)
      => new(
          "Tu membresía está activa",
          justSold
              ? $"Registramos tu membresía «{planName}» y ya está activa hasta el {CustomerNotificationOutbox.Date(endsOn)}."
              : $"Tu membresía «{planName}» está activa hasta el {CustomerNotificationOutbox.Date(endsOn)}.",
          MembershipUrl(membershipId),
          $"membership-{membershipId:N}",
          IsTimeSensitive: false);

  public static PushNotificationMessage MembershipConsumed(
      Guid membershipId, string planName, string serviceName, int quantity, int remaining)
  {
    var used = quantity == 1 ? $"«{serviceName}»" : $"{quantity} «{serviceName}»";
    var left = remaining switch
    {
      0 => "Ya no te quedan más de ese servicio.",
      1 => "Te queda 1 de ese servicio.",
      _ => $"Te quedan {remaining} de ese servicio."
    };

    return new(
        "Usaste tu membresía",
        $"Registramos {used} con tu membresía «{planName}». {left}",
        MembershipUrl(membershipId),
        $"membership-{membershipId:N}",
        IsTimeSensitive: false);
  }

  public static PushNotificationMessage MembershipExpiring(Guid membershipId, string planName, DateOnly endsOn, DateOnly today)
  {
    var days = endsOn.DayNumber - today.DayNumber;
    var when = days switch
    {
      0 => "hoy",
      1 => "mañana",
      _ => $"en {days} días"
    };

    return new(
        "Tu membresía vence pronto",
        $"Tu membresía «{planName}» vence {when} ({CustomerNotificationOutbox.Date(endsOn)}). Aprovecha lo que te queda.",
        MembershipUrl(membershipId),
        $"membership-expiring-{membershipId:N}",
        IsTimeSensitive: false);
  }

  public static PushNotificationMessage PointsEarned(int points, string reason)
      => new(
          $"Ganaste {CustomerNotificationOutbox.Points(points)}",
          reason,
          LoyaltyUrl,
          "loyalty-points",
          IsTimeSensitive: false);

  public static PushNotificationMessage PointsWithdrawn(int points, string reason)
      => new(
          $"Se retiraron {CustomerNotificationOutbox.Points(points)}",
          reason,
          LoyaltyUrl,
          "loyalty-points",
          IsTimeSensitive: false);

  public static PushNotificationMessage PointsReturned(int points, string reason)
      => new(
          $"Te devolvimos {CustomerNotificationOutbox.Points(points)}",
          reason,
          LoyaltyUrl,
          "loyalty-points",
          IsTimeSensitive: false);

  public static PushNotificationMessage RedemptionApproved(Guid redemptionId, string rewardName, DateOnly? deliverBy)
      => new(
          "Tu canje fue aprobado",
          deliverBy is { } lastDay
              ? $"Aprobamos tu canje de «{rewardName}». Reclámalo en la barbería hasta el {CustomerNotificationOutbox.Date(lastDay)}."
              : $"Aprobamos tu canje de «{rewardName}».",
          LoyaltyUrl,
          $"redemption-{redemptionId:N}",
          IsTimeSensitive: false);

  public static PushNotificationMessage RedemptionRejected(Guid redemptionId, string rewardName, int points, string reason)
      => new(
          "Tu canje no fue aprobado",
          $"No aprobamos tu canje de «{rewardName}». Motivo: {reason.Trim().TrimEnd('.')}. Tus {CustomerNotificationOutbox.Points(points)} vuelven a estar disponibles.",
          LoyaltyUrl,
          $"redemption-{redemptionId:N}",
          IsTimeSensitive: false);

  public static PushNotificationMessage PointsExpiring(int points, DateOnly expiresOn)
      => new(
          "Tienes puntos por vencer",
          $"{Capitalize(CustomerNotificationOutbox.Points(points))} de tu cuenta vencen el {CustomerNotificationOutbox.Date(expiresOn)}. Úsalos en un canje antes de esa fecha.",
          LoyaltyUrl,
          "loyalty-expiring",
          IsTimeSensitive: false);

  private static string Capitalize(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
