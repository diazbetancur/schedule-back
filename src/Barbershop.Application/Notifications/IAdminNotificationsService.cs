namespace Barbershop.Application.Notifications;

public interface IAdminNotificationsService
{
  Task<NotificationCampaignView> BroadcastAsync(Guid currentUserId, NotificationBroadcastRequest request, CancellationToken cancellationToken = default);

  Task<IReadOnlyList<NotificationCampaignView>> GetCampaignsAsync(CancellationToken cancellationToken = default);

  Task<IReadOnlyList<CustomerSummaryView>> SearchCustomersAsync(string? search, CancellationToken cancellationToken = default);
}
