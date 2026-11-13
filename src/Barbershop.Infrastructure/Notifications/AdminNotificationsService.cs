using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Notifications;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Notifications;

internal sealed class AdminNotificationsService : IAdminNotificationsService
{
  private static readonly string NormalizedCustomerRole = RoleNames.Customer.ToUpperInvariant();

  private readonly AppDbContext _dbContext;
  private readonly INotificationDispatcher _dispatcher;
  private readonly TimeProvider _timeProvider;

  public AdminNotificationsService(AppDbContext dbContext, INotificationDispatcher dispatcher, TimeProvider timeProvider)
  {
    _dbContext = dbContext;
    _dispatcher = dispatcher;
    _timeProvider = timeProvider;
  }

  public async Task<NotificationCampaignView> BroadcastAsync(Guid currentUserId, NotificationBroadcastRequest request, CancellationToken cancellationToken = default)
  {
    var targetType = NormalizeTargetType(request.TargetType);
    ValidateRequest(request, targetType);

    var sentByUser = await _dbContext.Users
        .SingleOrDefaultAsync(user => user.Id == currentUserId && user.IsActive, cancellationToken)
        ?? throw new KeyNotFoundException("No encontramos tu usuario. Inicia sesión de nuevo.");

    var (recipientUserIds, targetSummary) = await ResolveRecipientsAsync(request, targetType, cancellationToken);

    if (recipientUserIds.Count == 0)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["targetType"] = ["Ningún cliente activo coincide con estos destinatarios."]
      });
    }

    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
    var fingerprint = RecipientsFingerprint(recipientUserIds);
    var campaign = new NotificationCampaign(
        request.Title, request.Body, targetSummary, recipientUserIds.Count, currentUserId, nowUtc, fingerprint);

    // BUG-112: the same text to exactly the same recipients within a short window is a repeated click, not a new
    // campaign. The audience is compared by its fingerprint (sorted ids), so two different selections of the same
    // size are different campaigns. Serialised by a lock so two identical requests at once cannot both pass.
    await using (var transaction = _dbContext.Database.IsRelational()
        ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
        : null)
    {
      if (_dbContext.Database.IsNpgsql())
      {
        await _dbContext.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [BroadcastLockKey], cancellationToken);
      }

      var since = nowUtc - DuplicateWindow;
      var duplicated = await _dbContext.NotificationCampaigns.AnyAsync(
          previous => previous.CreatedAt >= since
              && previous.Title == campaign.Title
              && previous.Body == campaign.Body
              && previous.RecipientsFingerprint == fingerprint,
          cancellationToken);
      if (duplicated)
      {
        throw new ConflictException(
            $"Ya se envió esta misma notificación a este público hace menos de {DuplicateWindow.TotalMinutes:0} minutos. No se volvió a enviar.");
      }

      _dbContext.NotificationCampaigns.Add(campaign);
      await _dbContext.SaveChangesAsync(cancellationToken);
      if (transaction is not null)
      {
        await transaction.CommitAsync(cancellationToken);
      }
    }

    var message = new PushNotificationMessage(
        request.Title,
        request.Body,
        Url: "/",
        Tag: $"campaign-{campaign.Id:N}",
        IsTimeSensitive: false,
        TimeToLive: TimeSpan.FromDays(3));

    await _dispatcher.DispatchAsync(recipientUserIds, UserNotificationTypes.Campaign, message, cancellationToken);

    return Map(campaign, sentByUser.FullName);
  }

  /// <summary>How long an identical broadcast (same text and audience) is refused after the first one (BUG-112).</summary>
  internal static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(5);

  private const long BroadcastLockKey = 0x42524F4144; // "BROAD"

  /// <summary>SHA-256 (hex) of the recipient ids in a fixed order: the same set gives the same value.</summary>
  internal static string RecipientsFingerprint(IEnumerable<Guid> recipientUserIds)
  {
    var ordered = string.Join(',', recipientUserIds.Distinct().Order().Select(id => id.ToString("N")));
    return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ordered)));
  }

  public async Task<NotificationBroadcastPreviewView> PreviewAsync(
      NotificationBroadcastRequest request, CancellationToken cancellationToken = default)
  {
    var targetType = NormalizeTargetType(request.TargetType);
    ValidateRequest(request, targetType);
    var (recipientUserIds, targetSummary) = await ResolveRecipientsAsync(request, targetType, cancellationToken);
    return new NotificationBroadcastPreviewView(recipientUserIds.Count, targetSummary);
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

    if (string.IsNullOrWhiteSpace(search))
    {
      return await query
          .OrderBy(user => user.FullName)
          .Take(MaxSearchResults)
          .Select(user => new CustomerSummaryView(user.Id, user.FullName, user.Email, user.PhoneNumber))
          .ToListAsync(cancellationToken);
    }

    var normalizedSearch = search.Trim().ToUpperInvariant();
    var byNameOrEmail = await query
        .Where(user => user.FullName.ToUpper().Contains(normalizedSearch) || user.Email.ToUpper().Contains(normalizedSearch))
        .OrderBy(user => user.FullName)
        .Take(MaxSearchResults)
        .Select(user => new CustomerSummaryView(user.Id, user.FullName, user.Email, user.PhoneNumber))
        .ToListAsync(cancellationToken);

    // BUG-108: the field says «Nombre, correo o teléfono». Phones are free text ("+57 300 123 4567", "300-123-4567"),
    // so both sides are compared by their digits, without the country code 57.
    var searchDigits = NationalDigits(search);
    if (searchDigits.Length < MinPhoneSearchDigits)
    {
      return byNameOrEmail;
    }

    var withPhone = await query
        .Where(user => user.PhoneNumber != null)
        .Select(user => new CustomerSummaryView(user.Id, user.FullName, user.Email, user.PhoneNumber))
        .ToListAsync(cancellationToken);
    var byPhone = withPhone.Where(user => NationalDigits(user.PhoneNumber).Contains(searchDigits, StringComparison.Ordinal));

    return byNameOrEmail
        .Concat(byPhone)
        .DistinctBy(user => user.Id)
        .OrderBy(user => user.FullName, StringComparer.CurrentCultureIgnoreCase)
        .Take(MaxSearchResults)
        .ToList();
  }

  private const int MaxSearchResults = 25;

  /// <summary>Fewer digits than this would match almost any phone.</summary>
  private const int MinPhoneSearchDigits = 4;

  /// <summary>Only the digits, without the Colombian country code when it is written (57 + 10-digit number).</summary>
  internal static string NationalDigits(string? phone)
  {
    var digits = new string((phone ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
    if (digits.StartsWith("0057", StringComparison.Ordinal))
    {
      digits = digits[2..];
    }

    return digits.Length == 12 && digits.StartsWith("57", StringComparison.Ordinal) ? digits[2..] : digits;
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
            ?? throw new KeyNotFoundException("No encontramos este profesional.");

        // BUG-113: the same customers as the other targets (active accounts with the Customer role) that have at least
        // one appointment with the professional that was not cancelled.
        var staffCustomerIds = _dbContext.Appointments
            .Where(appointment => appointment.StaffProfileId == staffProfile.Id
                && appointment.CustomerUserId != null
                && appointment.Status != AppointmentStatus.Cancelled)
            .Select(appointment => appointment.CustomerUserId!.Value);
        var filteredIds = await _dbContext.Users
            .Where(user => user.IsActive
                && staffCustomerIds.Contains(user.Id)
                && user.UserRoles.Any(userRole => userRole.Role.NormalizedName == NormalizedCustomerRole))
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);

        return (filteredIds, $"Clientes de {staffProfile.DisplayName}");

      default:
        throw new ValidationProblemException(new Dictionary<string, string[]>
        {
          ["targetType"] = ["Elige a quién enviar la notificación."]
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
      errors["title"] = ["El título es obligatorio."];
    }
    else if (request.Title.Length > 160)
    {
      errors["title"] = ["El título no puede superar 160 caracteres."];
    }

    if (string.IsNullOrWhiteSpace(request.Body))
    {
      errors["body"] = ["El mensaje es obligatorio."];
    }
    else if (request.Body.Length > 1000)
    {
      errors["body"] = ["El mensaje no puede superar 1000 caracteres."];
    }

    if (targetType is not (NotificationTargetTypes.All or NotificationTargetTypes.Selected or NotificationTargetTypes.Filter))
    {
      errors["targetType"] = ["Elige a quién enviar la notificación."];
    }
    else if (targetType == NotificationTargetTypes.Selected && (request.CustomerUserIds is null || request.CustomerUserIds.Count == 0))
    {
      errors["customerUserIds"] = ["Selecciona al menos un cliente."];
    }
    else if (targetType == NotificationTargetTypes.Filter && request.StaffProfileId is null)
    {
      errors["staffProfileId"] = ["Elige un profesional."];
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
