using Barbershop.Application.Notifications;

namespace Barbershop.Tests.Features.Notifications;

internal sealed class RecordingNotificationDispatcher : INotificationDispatcher
{
  public List<(IReadOnlyCollection<Guid> UserIds, string Type, PushNotificationMessage Message, NotificationDispatchOptions? Options)> Calls { get; } = [];

  public Task DispatchAsync(
      IReadOnlyCollection<Guid> userIds,
      string type,
      PushNotificationMessage message,
      CancellationToken cancellationToken = default)
  {
    Calls.Add((userIds, type, message, null));
    return Task.CompletedTask;
  }

  public Task<int> DispatchAsync(
      IReadOnlyCollection<Guid> userIds,
      string type,
      PushNotificationMessage message,
      NotificationDispatchOptions options,
      CancellationToken cancellationToken = default)
  {
    Calls.Add((userIds, type, message, options));
    return Task.FromResult(userIds.Count);
  }
}

internal sealed class FakePushDeliveryChannel : IPushDeliveryChannel
{
  public Func<Guid, IReadOnlyCollection<Guid>, PushChannelResult> Respond { get; set; }
      = (_, _) => new PushChannelResult(true, 1, [Guid.NewGuid()], 0, 0, null);

  public List<(Guid UserId, PushNotificationMessage Message, IReadOnlyCollection<Guid> Skipped)> Calls { get; } = [];

  public Task<PushChannelResult> SendToUserAsync(
      Guid userId,
      PushNotificationMessage message,
      IReadOnlyCollection<Guid> skipSubscriptionIds,
      CancellationToken cancellationToken = default)
  {
    Calls.Add((userId, message, skipSubscriptionIds));
    return Task.FromResult(Respond(userId, skipSubscriptionIds));
  }
}

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
  public DateTimeOffset Now { get; set; } = utcNow;

  public override DateTimeOffset GetUtcNow() => Now;

  public void Advance(TimeSpan by) => Now = Now.Add(by);
}
