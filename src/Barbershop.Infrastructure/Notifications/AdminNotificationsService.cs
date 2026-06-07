using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Notifications;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Notifications;

internal sealed class AdminNotificationsService : IAdminNotificationsService
{
  private static readonly string NormalizedCustomerRole = RoleNames.Customer.ToUpperInvariant();

  private readonly AppDbContext _dbContext;
  private readonly IPushNotificationSender _sender;
  private readonly TimeProvider _timeProvider;

  public AdminNotificationsService(AppDbContext dbContext, IPushNotificationSender sender, TimeProvider timeProvider)
  {
    _dbContext = dbContext;
    _sender = sender;
    _timeProvider = timeProvider;
  }

  public async Task<NotificationCampaignView> BroadcastAsync(Guid currentUserId, NotificationBroadcastRequest request, CancellationToken cancellationToken = default)
  {
    var targetType = NormalizeTargetType(request.TargetType);
    ValidateRequest(request, targetType);

    var sentByUser = await _dbContext.Users
        .SingleOrDefaultAsync(user => user.Id == currentUserId && user.IsActive, cancellationToken)
        ?? throw new KeyNotFoundException("The current user was not found.");

    var (recipientUserIds, targetSummary) = await ResolveRecipientsAsync(request, targetType, cancellationToken);

    if (recipientUserIds.Count == 0)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["targetType"] = ["No matching customers were found for the selected target."]
      });
    }

    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
    var campaign = new NotificationCampaign(request.Title, request.Body, targetSummary, recipientUserIds.Count, currentUserId, nowUtc);

    _dbContext.NotificationCampaigns.Add(campaign);
    await _dbContext.SaveChangesAsync(cancellationToken);

    await _sender.SendToUsersAsync(recipientUserIds, new PushNotificationMessage(request.Title, request.Body), cancellationToken);

    return Map(campaign, sentByUser.FullName);
  }

  public async Task<IReadOnlyList<NotificationCampaignView>> GetCampaignsAsync(CancellationToken cancellationToken = default)
  {
    var campaigns = await _dbContext.NotificationCampaigns
        .Include(campaign => campaign.SentByUser)
        .OrderByDescending(campaign => campaign.CreatedAt)
        .ToListAsync(cancellationToken);

    return campaigns.Select(campaign => Map(campaign, campaign.SentByUser.FullName)).ToArray();
  }

  public async Task<IReadOnlyList<CustomerSummaryView>> SearchCustomersAsync(string? search, CancellationToken cancellationToken = default)
  {
    var query = _dbContext.Users
        .Where(user => user.IsActive && user.UserRoles.Any(userRole => userRole.Role.NormalizedName == NormalizedCustomerRole));

    if (!string.IsNullOrWhiteSpace(search))
    {
      var normalizedSearch = search.Trim().ToUpperInvariant();
      query = query.Where(user =>
          user.FullName.ToUpper().Contains(normalizedSearch)
          || user.Email.ToUpper().Contains(normalizedSearch));
    }

    return await query
        .OrderBy(user => user.FullName)
        .Take(25)
        .Select(user => new CustomerSummaryView(user.Id, user.FullName, user.Email, user.PhoneNumber))
        .ToListAsync(cancellationToken);
  }

  private async Task<(IReadOnlyCollection<Guid> RecipientUserIds, string TargetSummary)> ResolveRecipientsAsync(
      NotificationBroadcastRequest request,
      string targetType,
      CancellationToken cancellationToken)
  {
    switch (targetType)
    {
      case NotificationTargetTypes.All:
        var allCustomerIds = await _dbContext.Users
            .Where(user => user.IsActive && user.UserRoles.Any(userRole => userRole.Role.NormalizedName == NormalizedCustomerRole))
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);

        return (allCustomerIds, "Todos los clientes");

      case NotificationTargetTypes.Selected:
        var selectedIds = request.CustomerUserIds ?? [];
        var matchedIds = await _dbContext.Users
            .Where(user => user.IsActive
                && selectedIds.Contains(user.Id)
                && user.UserRoles.Any(userRole => userRole.Role.NormalizedName == NormalizedCustomerRole))
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);

        return (matchedIds, $"{matchedIds.Count} cliente(s) seleccionado(s)");

      case NotificationTargetTypes.Filter:
        var staffProfile = await _dbContext.StaffProfiles
            .SingleOrDefaultAsync(profile => profile.Id == request.StaffProfileId!.Value, cancellationToken)
            ?? throw new KeyNotFoundException("The staff profile was not found.");

        var filteredIds = await _dbContext.Appointments
            .Where(appointment => appointment.StaffProfileId == staffProfile.Id && appointment.CustomerUserId != null)
            .Select(appointment => appointment.CustomerUserId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        return (filteredIds, $"Clientes de {staffProfile.DisplayName}");

      default:
        throw new ValidationProblemException(new Dictionary<string, string[]>
        {
          ["targetType"] = ["TargetType must be 'all', 'selected', or 'filter'."]
        });
    }
  }

  private static string NormalizeTargetType(string targetType)
      => (targetType ?? string.Empty).Trim().ToLowerInvariant();

  private static void ValidateRequest(NotificationBroadcastRequest request, string targetType)
  {
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    if (string.IsNullOrWhiteSpace(request.Title))
    {
      errors["title"] = ["Title is required."];
    }
    else if (request.Title.Length > 160)
    {
      errors["title"] = ["Title must be at most 160 characters."];
    }

    if (string.IsNullOrWhiteSpace(request.Body))
    {
      errors["body"] = ["Body is required."];
    }
    else if (request.Body.Length > 1000)
    {
      errors["body"] = ["Body must be at most 1000 characters."];
    }

    if (targetType is not (NotificationTargetTypes.All or NotificationTargetTypes.Selected or NotificationTargetTypes.Filter))
    {
      errors["targetType"] = ["TargetType must be 'all', 'selected', or 'filter'."];
    }
    else if (targetType == NotificationTargetTypes.Selected && (request.CustomerUserIds is null || request.CustomerUserIds.Count == 0))
    {
      errors["customerUserIds"] = ["CustomerUserIds is required when TargetType is 'selected'."];
    }
    else if (targetType == NotificationTargetTypes.Filter && request.StaffProfileId is null)
    {
      errors["staffProfileId"] = ["StaffProfileId is required when TargetType is 'filter'."];
    }

    if (errors.Count > 0)
    {
      throw new ValidationProblemException(errors);
    }
  }

  private static NotificationCampaignView Map(NotificationCampaign campaign, string sentByName)
      => new(
          campaign.Id,
          campaign.Title,
          campaign.Body,
          campaign.TargetSummary,
          campaign.RecipientCount,
          sentByName,
          campaign.CreatedAt);
}
