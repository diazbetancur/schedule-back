namespace Barbershop.Application.Notifications;

public interface IAdminNotificationsService
{
  Task<NotificationCampaignView> BroadcastAsync(Guid currentUserId, NotificationBroadcastRequest request, CancellationToken cancellationToken = default);

  Task<IReadOnlyList<NotificationCampaignView>> GetCampaignsAsync(CancellationToken cancellationToken = default);

  /// <summary>Recipients of a broadcast without sending it (BUG-110).</summary>
  Task<NotificationBroadcastPreviewView> PreviewAsync(NotificationBroadcastRequest request, CancellationToken cancellationToken = default);

  Task<IReadOnlyList<CustomerSummaryView>> SearchCustomersAsync(string? search, CancellationToken cancellationToken = default);
}
