using Barbershop.Application.Notifications;
using Barbershop.Domain.Common;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.Memberships;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Notifications;

/// <summary>
/// ADR-0019 N3: run by <see cref="NotificationBackgroundWorker"/> every 15 minutes, between 08:00 and 20:00 in Bogotá.
/// <list type="bullet">
/// <item>An active membership that ends in <see cref="LeadDays"/> days or less (and had a longer validity).</item>
/// <item>Free points (reserved ones are not part of the balance) of lots that expire in <see cref="LeadDays"/> days or less (program enabled, longer validity),
/// one notice per customer and expiry date.</item>
/// </list>
/// Idempotent: each notice has a dedup key (unique per user in the database), so running it again never duplicates.
/// </summary>
internal sealed class CustomerExpiryReminderService
{
  internal const int LeadDays = 7;
  internal const int SendFromHour = 8;
  internal const int SendUntilHour = 20;

  private readonly AppDbContext _dbContext;
  private readonly INotificationDispatcher _dispatcher;
  private readonly TimeProvider _timeProvider;

  public CustomerExpiryReminderService(AppDbContext dbContext, INotificationDispatcher dispatcher, TimeProvider timeProvider)
  {
    _dbContext = dbContext;
    _dispatcher = dispatcher;
    _timeProvider = timeProvider;
  }

  /// <returns>How many notifications were created.</returns>
  public async Task<int> RunAsync(CancellationToken cancellationToken = default)
  {
    var localNow = BogotaClock.ToLocal(_timeProvider.GetUtcNow().UtcDateTime);
    if (localNow.Hour < SendFromHour || localNow.Hour >= SendUntilHour)
    {
      return 0;
    }

    var today = DateOnly.FromDateTime(localNow);
    var created = await SendMembershipRemindersAsync(today, cancellationToken);
    created += await SendPointsRemindersAsync(today, cancellationToken);
    return created;
  }

  private async Task<int> SendMembershipRemindersAsync(DateOnly today, CancellationToken cancellationToken)
  {
    var horizon = today.AddDays(LeadDays);
    var memberships = await _dbContext.CustomerMemberships.AsNoTracking()
        .Where(membership => membership.Status == MembershipStatus.Active
            && membership.EndsOn >= today
            && membership.EndsOn <= horizon
            // SONDA BUG-164: a membership with every unit already used has nothing left to lose; no warning.
            && _dbContext.MembershipAllowances.Where(allowance => allowance.CustomerMembershipId == membership.Id).Sum(allowance => allowance.GrantedQuantity)
                > _dbContext.MembershipMovements.Where(movement => movement.CustomerMembershipId == membership.Id)
                    .Sum(movement => movement.Type == MembershipMovementType.Consumption ? movement.Quantity : -movement.Quantity))
        .Join(
            _dbContext.Customers.AsNoTracking().Where(profile => profile.UserId != null),
            membership => membership.CustomerId,
            profile => profile.Id,
            (membership, profile) => new
            {
              membership.Id,
              membership.PlanName,
              membership.StartsOn,
              membership.EndsOn,
              UserId = profile.UserId!.Value,
            })
        .ToListAsync(cancellationToken);

    var candidates = memberships
        // A membership shorter than the lead time would be warned right after its sale: noise.
        .Where(membership => membership.EndsOn.DayNumber - membership.StartsOn.DayNumber + 1 > LeadDays)
        .Select(membership => (
            membership.UserId,
            DedupKey: $"membership-expiring:{membership.Id:N}:{membership.EndsOn:yyyyMMdd}",
            Message: CustomerNotificationMessages.MembershipExpiring(membership.Id, membership.PlanName, membership.EndsOn, today)))
        .ToList();

    return await DispatchNewAsync(candidates, UserNotificationTypes.MembershipExpiring, cancellationToken);
  }

  private async Task<int> SendPointsRemindersAsync(DateOnly today, CancellationToken cancellationToken)
  {
    var programEnabled = await _dbContext.LoyaltySettings.AsNoTracking()
        .AnyAsync(settings => settings.Id == LoyaltySettings.SingletonId && settings.IsEnabled, cancellationToken);
    if (!programEnabled)
    {
      return 0;
    }

    var horizon = today.AddDays(LeadDays);
    var expiring = await _dbContext.LoyaltyLots.AsNoTracking()
        .Where(lot => lot.RemainingPoints > 0
            && lot.ExpiresOn >= today
            && lot.ExpiresOn <= horizon
            && lot.ValidityDays > LeadDays)
        .GroupBy(lot => new { lot.CustomerId, lot.ExpiresOn })
        .Select(group => new
        {
          group.Key.CustomerId,
          group.Key.ExpiresOn,
          Points = group.Sum(lot => lot.RemainingPoints),
        })
        .Join(
            _dbContext.Customers.AsNoTracking().Where(profile => profile.UserId != null),
            group => group.CustomerId,
            profile => profile.Id,
            (group, profile) => new { group.CustomerId, group.ExpiresOn, group.Points, UserId = profile.UserId!.Value })
        .ToListAsync(cancellationToken);

    var candidates = expiring
        .Select(group => (
            group.UserId,
            DedupKey: $"points-expiring:{group.CustomerId:N}:{group.ExpiresOn:yyyyMMdd}",
            Message: CustomerNotificationMessages.PointsExpiring(group.Points, group.ExpiresOn)))
        .ToList();

    return await DispatchNewAsync(candidates, UserNotificationTypes.LoyaltyPointsExpiring, cancellationToken);
  }

  private async Task<int> DispatchNewAsync(
      IReadOnlyList<(Guid UserId, string DedupKey, PushNotificationMessage Message)> candidates,
      string type,
      CancellationToken cancellationToken)
  {
    if (candidates.Count == 0)
    {
      return 0;
    }

    // One query to skip what was already sent (the dispatcher checks again; this avoids one query per candidate).
    var keys = candidates.Select(candidate => NotificationDispatcher.BuildDedupKey(candidate.DedupKey, candidate.UserId)).ToList();
    var existing = (await _dbContext.UserNotifications.AsNoTracking()
            .Where(notification => notification.DedupKey != null && keys.Contains(notification.DedupKey))
            .Select(notification => notification.DedupKey!)
            .ToListAsync(cancellationToken))
        .ToHashSet(StringComparer.Ordinal);

    var created = 0;
    foreach (var (userId, dedupKey, message) in candidates)
    {
      if (existing.Contains(NotificationDispatcher.BuildDedupKey(dedupKey, userId)))
      {
        continue;
      }

      created += await _dispatcher.DispatchAsync([userId], type, message, new NotificationDispatchOptions(dedupKey), cancellationToken);
    }

    return created;
  }
}
