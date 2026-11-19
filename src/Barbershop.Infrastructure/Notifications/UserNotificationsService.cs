using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Common.Paging;
using Barbershop.Application.Notifications;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Notifications;

internal sealed class UserNotificationsService : IUserNotificationsService
{
  internal const int DefaultLimit = 5;
  internal const int MaxLimit = 50;

  private readonly AppDbContext _dbContext;
  private readonly TimeProvider _timeProvider;

  public UserNotificationsService(AppDbContext dbContext, TimeProvider timeProvider)
  {
    _dbContext = dbContext;
    _timeProvider = timeProvider;
  }

  public async Task<UserNotificationInboxView> GetInboxAsync(
      Guid currentUserId, int limit, CancellationToken cancellationToken = default, int offset = 0)
  {
    var take = limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);
    var skip = Math.Max(0, offset);

    var unreadCount = await _dbContext.UserNotifications
        .CountAsync(notification => notification.UserId == currentUserId && notification.ReadAt == null, cancellationToken);

    // BUG-107: the badge counted every unread notification while the panel only showed the 5 most recent (often
    // already read). Unread ones come first, then the most recent; one more row than asked tells whether there is more.
    var items = await _dbContext.UserNotifications
        .AsNoTracking()
        .Where(notification => notification.UserId == currentUserId)
        .OrderBy(notification => notification.ReadAt == null ? 0 : 1)
        .ThenByDescending(notification => notification.CreatedAt)
        .ThenByDescending(notification => notification.Id)
        .Skip(skip)
        .Take(take + 1)
        .Select(notification => new UserNotificationView(
            notification.Id,
            notification.Type,
            notification.Title,
            notification.Body,
            notification.Url,
            notification.CreatedAt,
            notification.ReadAt))
        .ToListAsync(cancellationToken);

    var hasMore = items.Count > take;
    return new UserNotificationInboxView(unreadCount, hasMore ? items.Take(take).ToList() : items, hasMore);
  }

  public async Task MarkAsReadAsync(Guid currentUserId, Guid notificationId, CancellationToken cancellationToken = default)
  {
    var notification = await _dbContext.UserNotifications
        .SingleOrDefaultAsync(
            candidate => candidate.Id == notificationId && candidate.UserId == currentUserId,
            cancellationToken)
        ?? throw new KeyNotFoundException("No encontramos la notificación.");

    if (notification.ReadAt is not null)
    {
      return;
    }

    notification.MarkAsRead(_timeProvider.GetUtcNow().UtcDateTime);
    await _dbContext.SaveChangesAsync(cancellationToken);
  }

  public async Task MarkAllAsReadAsync(Guid currentUserId, CancellationToken cancellationToken = default)
  {
    var unread = await _dbContext.UserNotifications
        .Where(notification => notification.UserId == currentUserId && notification.ReadAt == null)
        .ToListAsync(cancellationToken);

    if (unread.Count == 0)
    {
      return;
    }

    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
    foreach (var notification in unread)
    {
      notification.MarkAsRead(nowUtc);
    }

    await _dbContext.SaveChangesAsync(cancellationToken);
  }

  internal const int HistoryDefaultPageSize = 20;
  internal const int HistoryMaxPageSize = 50;

  public async Task<UserNotificationHistoryView> GetHistoryAsync(
      Guid currentUserId, int? page, int? pageSize, bool unreadOnly, CancellationToken cancellationToken = default)
  {
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
    if (page is < 1 or > PageRequest.MaxPage)
    {
      errors["page"] = ["La página debe estar entre 1 y 1.000.000."];
    }

    if (pageSize is < 1 or > HistoryMaxPageSize)
    {
      errors["pageSize"] = [$"El tamaño de página debe estar entre 1 y {HistoryMaxPageSize}."];
    }

    if (errors.Count > 0)
    {
      throw new ValidationProblemException(errors);
    }

    var request = PageRequest.Create(page ?? 1, pageSize ?? HistoryDefaultPageSize);
    var mine = _dbContext.UserNotifications.AsNoTracking().Where(notification => notification.UserId == currentUserId);
    var unreadCount = await mine.CountAsync(notification => notification.ReadAt == null, cancellationToken);
    var filtered = unreadOnly ? mine.Where(notification => notification.ReadAt == null) : mine;
    var totalCount = unreadOnly ? unreadCount : await filtered.CountAsync(cancellationToken);

    // ADR-0019 N5: a history reads in time order (newest first), unlike the bell, which puts unread first (BUG-107).
    var items = await filtered
        .OrderByDescending(notification => notification.CreatedAt)
        .ThenByDescending(notification => notification.Id)
        .Skip(request.Skip)
        .Take(request.PageSize)
        .Select(notification => new UserNotificationView(
            notification.Id,
            notification.Type,
            notification.Title,
            notification.Body,
            notification.Url,
            notification.CreatedAt,
            notification.ReadAt))
        .ToListAsync(cancellationToken);

    return new UserNotificationHistoryView(items, request.Page, request.PageSize, totalCount, unreadCount);
  }

  public async Task<NotificationPreferencesView> GetPreferencesAsync(Guid currentUserId, CancellationToken cancellationToken = default)
  {
    var stored = await _dbContext.UserNotificationPreferences.AsNoTracking()
        .Where(preference => preference.UserId == currentUserId)
        .ToDictionaryAsync(preference => preference.Category, preference => preference.PushEnabled, StringComparer.Ordinal, cancellationToken);

    // ADR-0021 L4: «Reseñas» is only offered to professionals and administrators, the only ones who receive it.
    var isStaffSide = await IsStaffSideAsync(currentUserId, cancellationToken);

    // ADR-0019 N4: without a row the category is on.
    return new NotificationPreferencesView(NotificationCategories.Optional
        .Where(category => isStaffSide || !NotificationCategories.StaffOnly.Contains(category))
        .Select(category => new NotificationPreferenceView(category, stored.GetValueOrDefault(category, true)))
        .ToArray());
  }

  internal const string StaffOnlyCategoryMessage = "Los avisos de reseñas son solo para profesionales y administradores.";

  private async Task<bool> IsStaffSideAsync(Guid currentUserId, CancellationToken cancellationToken)
  {
    var normalizedStaffRoles = new[] { RoleNames.Admin.ToUpperInvariant(), RoleNames.Staff.ToUpperInvariant() };
    return await _dbContext.Users.AsNoTracking()
        .AnyAsync(user => user.Id == currentUserId
            && user.UserRoles.Any(userRole => normalizedStaffRoles.Contains(userRole.Role.NormalizedName)), cancellationToken);
  }

  public async Task<NotificationPreferencesView> UpdatePreferencesAsync(
      Guid currentUserId, NotificationPreferencesUpdateRequest request, CancellationToken cancellationToken = default)
  {
    var changes = request.Categories ?? [];
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
    if (changes.Count == 0)
    {
      errors["categories"] = ["Indica al menos un tipo de aviso."];
    }

    for (var index = 0; index < changes.Count; index++)
    {
      var change = changes[index];
      if (change?.Category is not { } category || !NotificationCategories.IsOptional(category))
      {
        errors[$"categories[{index}].category"] =
            ["Tipo de aviso no válido: appointments, memberships, loyalty, purchases, reviews o campaigns. Los avisos de cuenta y seguridad no se pueden apagar."];
      }

      if (change?.PushEnabled is null)
      {
        errors[$"categories[{index}].pushEnabled"] = ["Indica si quieres recibir este tipo de aviso."];
      }
    }

    if (changes.Where(change => change?.Category is not null).GroupBy(change => change!.Category).Any(group => group.Count() > 1))
    {
      errors["categories"] = ["Cada tipo de aviso puede aparecer una sola vez."];
    }

    if (errors.Count == 0 && changes.Any(change => NotificationCategories.StaffOnly.Contains(change!.Category!, StringComparer.Ordinal))
        && !await IsStaffSideAsync(currentUserId, cancellationToken))
    {
      // SONDA BUG-176 (ADR-0021 L4): «Reseñas» only reaches professionals and administrators; a customer cannot store it.
      for (var index = 0; index < changes.Count; index++)
      {
        if (NotificationCategories.StaffOnly.Contains(changes[index]!.Category!, StringComparer.Ordinal))
        {
          errors[$"categories[{index}].category"] = [StaffOnlyCategoryMessage];
        }
      }
    }

    if (errors.Count > 0)
    {
      throw new ValidationProblemException(errors);
    }

    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
    var categories = changes.Select(change => change!.Category!).ToArray();
    var stored = await _dbContext.UserNotificationPreferences
        .Where(preference => preference.UserId == currentUserId && categories.Contains(preference.Category))
        .ToDictionaryAsync(preference => preference.Category, StringComparer.Ordinal, cancellationToken);

    foreach (var change in changes)
    {
      if (stored.TryGetValue(change!.Category!, out var preference))
      {
        preference.Update(change.PushEnabled!.Value, nowUtc);
      }
      else
      {
        _dbContext.UserNotificationPreferences.Add(
            new UserNotificationPreference(currentUserId, change.Category!, change.PushEnabled!.Value, nowUtc));
      }
    }

    try
    {
      await _dbContext.SaveChangesAsync(cancellationToken);
    }
    catch (DbUpdateException)
    {
      // Two saves of the same new category at once: the other one stored it; the user can simply save again.
      _dbContext.ChangeTracker.Clear();
      throw new ConflictException("Tus preferencias cambiaron desde otra pestaña. Recarga e intenta de nuevo.");
    }

    return await GetPreferencesAsync(currentUserId, cancellationToken);
  }
}
