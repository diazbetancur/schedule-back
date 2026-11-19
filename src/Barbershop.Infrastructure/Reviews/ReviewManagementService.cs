using System.Globalization;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Notifications;
using Barbershop.Application.Reviews;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Common;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Notifications;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Reviews;

internal sealed class ReviewManagementService : ICustomerReviewsService, IPublicReviewsService
{
  private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("es-CO");
  private static readonly string NormalizedAdminRole = RoleNames.Admin.ToUpperInvariant();

  private readonly AppDbContext _dbContext;
  private readonly TimeProvider _timeProvider;
  private readonly CustomerNotificationOutbox _notifications;

  public ReviewManagementService(AppDbContext dbContext, TimeProvider timeProvider)
  {
    _dbContext = dbContext;
    _timeProvider = timeProvider;
    _notifications = new CustomerNotificationOutbox(dbContext, timeProvider);
  }

  async Task<IReadOnlyList<CustomerReviewView>> ICustomerReviewsService.GetByCurrentCustomerAsync(Guid currentUserId, CancellationToken cancellationToken)
  {
    _ = await LoadActiveUserAsync(currentUserId, cancellationToken);

    var reviews = await _dbContext.Reviews
        .AsNoTracking()
        .Where(review => review.CustomerUserId == currentUserId)
        .OrderByDescending(review => review.CreatedAt)
        .Select(review => new CustomerReviewView(
            review.Id,
            review.AppointmentId,
            review.Appointment.StaffProfileId,
            review.Stars,
            review.Comment,
            review.CreatedAt))
        .ToListAsync(cancellationToken);

    return reviews;
  }

  async Task<CustomerReviewView> ICustomerReviewsService.CreateAsync(
      Guid currentUserId,
      Guid appointmentId,
      ReviewCreateRequest request,
      CancellationToken cancellationToken)
  {
    ValidateCreateRequest(appointmentId, request);

    _ = await LoadActiveUserAsync(currentUserId, cancellationToken);

    var appointment = await _dbContext.Appointments
        .SingleOrDefaultAsync(
            candidate => candidate.Id == appointmentId && candidate.CustomerUserId == currentUserId,
            cancellationToken)
        ?? throw new KeyNotFoundException("No encontramos la cita.");

    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    var isTerminalNonReviewable = appointment.Status is AppointmentStatus.Cancelled or AppointmentStatus.NoShow;
    var isPastEndTime = appointment.EndsAt <= nowUtc;

    if (isTerminalNonReviewable || (!isPastEndTime && appointment.Status is not AppointmentStatus.Completed))
    {
      errors["appointmentId"] = ["Solo puedes calificar citas completadas."];
    }

    var hasExistingReview = await _dbContext.Reviews
        .AnyAsync(review => review.AppointmentId == appointmentId, cancellationToken);

    if (hasExistingReview)
    {
      errors["appointmentId"] = ["Ya calificaste esta cita."];
    }

    ThrowIfAnyErrors(errors);

    if (appointment.Status is not AppointmentStatus.Completed)
    {
      appointment.UpdateStatus(AppointmentStatus.Completed, nowUtc);
    }

    var review = new Review(
        appointment.Id,
        currentUserId,
        request.Stars,
        _timeProvider.GetUtcNow().UtcDateTime,
        request.Comment);

    _dbContext.Reviews.Add(review);
    await QueueReviewNoticesAsync(appointment, review, currentUserId, cancellationToken);

    try
    {
      await _dbContext.SaveChangesAsync(cancellationToken);
    }
    catch (DbUpdateException)
    {
      var reviewExists = await _dbContext.Reviews
          .AsNoTracking()
          .AnyAsync(candidate => candidate.AppointmentId == appointmentId, cancellationToken);

      if (reviewExists)
      {
        throw new ValidationProblemException(new Dictionary<string, string[]>
        {
          ["appointmentId"] = ["Ya calificaste esta cita."]
        });
      }

      throw;
    }

    return MapCustomerReview(review, appointment.StaffProfileId);
  }

  /// <summary>
  /// ADR-0021 L4: the professional of the appointment and every active administrator are told about the review (stars
  /// and comment), in the same unit of work as the review: if it is not saved, nobody is notified. Nobody gets it twice
  /// and the author never gets their own review. Category «Reseñas»: the push follows each user's preferences.
  /// </summary>
  private async Task QueueReviewNoticesAsync(Appointment appointment, Review review, Guid authorUserId, CancellationToken cancellationToken)
  {
    var staff = await _dbContext.StaffProfiles.AsNoTracking()
        .Where(profile => profile.Id == appointment.StaffProfileId)
        .Select(profile => new { profile.UserId, profile.DisplayName })
        .SingleOrDefaultAsync(cancellationToken);
    var staffUserId = staff is null
        ? (Guid?)null
        : await _dbContext.Users.AsNoTracking()
            .Where(user => user.Id == staff.UserId && user.IsActive)
            .Select(user => (Guid?)user.Id)
            .SingleOrDefaultAsync(cancellationToken);
    var adminUserIds = await _dbContext.Users.AsNoTracking()
        .Where(user => user.IsActive && user.UserRoles.Any(userRole => userRole.Role.NormalizedName == NormalizedAdminRole))
        .Select(user => user.Id)
        .ToListAsync(cancellationToken);

    var staffName = staff?.DisplayName ?? "el profesional";
    var when = BogotaClock.ToLocal(appointment.StartsAt).ToString("d 'de' MMMM", DisplayCulture);
    var stars = review.Stars == 1 ? "1 de 5 estrellas" : $"{review.Stars} de 5 estrellas";
    var comment = string.IsNullOrWhiteSpace(review.Comment) ? null : review.Comment.Trim();
    var title = $"Nueva reseña: {stars}";
    var url = $"/staff/{appointment.StaffProfileId}";
    var tag = $"review-{review.Id:N}";

    if (staffUserId is { } staffId && staffId != authorUserId)
    {
      var staffBody = comment is null
          ? $"{appointment.CustomerName} calificó tu cita del {when} y no dejó comentario."
          : $"{appointment.CustomerName} calificó tu cita del {when}: «{comment}»";
      _notifications.Queue(staffId, UserNotificationTypes.ReviewReceived, new PushNotificationMessage(title, staffBody, url, tag, IsTimeSensitive: false));
    }

    var adminBody = comment is null
        ? $"{appointment.CustomerName} calificó la cita del {when} con {staffName} y no dejó comentario."
        : $"{appointment.CustomerName} calificó la cita del {when} con {staffName}: «{comment}»";
    foreach (var adminId in adminUserIds.Where(id => id != authorUserId && id != staffUserId).Distinct())
    {
      _notifications.Queue(adminId, UserNotificationTypes.ReviewReceived, new PushNotificationMessage(title, adminBody, url, tag, IsTimeSensitive: false));
    }
  }

  async Task<IReadOnlyList<PublicStaffReviewView>> IPublicReviewsService.GetByStaffProfileAsync(Guid staffProfileId, CancellationToken cancellationToken)
  {
    _ = await LoadActiveStaffProfileAsync(staffProfileId, cancellationToken);

    var reviews = await _dbContext.Reviews
        .AsNoTracking()
        .Where(review => review.Appointment.StaffProfileId == staffProfileId && review.Appointment.Status == AppointmentStatus.Completed)
        .OrderByDescending(review => review.CreatedAt)
        .Select(review => new PublicStaffReviewView(
            review.Stars,
            review.Comment,
            review.CreatedAt))
        .ToListAsync(cancellationToken);

    return reviews;
  }

  async Task<PublicStaffReviewsSummaryView> IPublicReviewsService.GetSummaryByStaffProfileAsync(Guid staffProfileId, CancellationToken cancellationToken)
  {
    _ = await LoadActiveStaffProfileAsync(staffProfileId, cancellationToken);

    var summary = await _dbContext.Reviews
        .AsNoTracking()
        .Where(review => review.Appointment.StaffProfileId == staffProfileId && review.Appointment.Status == AppointmentStatus.Completed)
        .GroupBy(_ => 1)
        .Select(group => new
        {
          TotalReviews = group.Count(),
          AverageStars = group.Average(review => (decimal)review.Stars)
        })
        .SingleOrDefaultAsync(cancellationToken);

    if (summary is null)
    {
      return new PublicStaffReviewsSummaryView(staffProfileId, 0, 0m);
    }

    return new PublicStaffReviewsSummaryView(
        staffProfileId,
        summary.TotalReviews,
        decimal.Round(summary.AverageStars, 2, MidpointRounding.AwayFromZero));
  }

  private async Task<User> LoadActiveUserAsync(Guid userId, CancellationToken cancellationToken)
  {
    return await _dbContext.Users
        .AsNoTracking()
        .SingleOrDefaultAsync(user => user.Id == userId && user.IsActive, cancellationToken)
        ?? throw new KeyNotFoundException("No encontramos tu usuario. Inicia sesión de nuevo.");
  }

  private async Task<StaffProfile> LoadActiveStaffProfileAsync(Guid staffProfileId, CancellationToken cancellationToken)
  {
    var staffProfile = await _dbContext.StaffProfiles
        .Include(profile => profile.User)
        .AsNoTracking()
        .SingleOrDefaultAsync(profile => profile.Id == staffProfileId, cancellationToken)
        ?? throw new KeyNotFoundException("No encontramos este profesional.");

    if (!staffProfile.IsActive || !staffProfile.User.IsActive)
    {
      throw new KeyNotFoundException("No encontramos este profesional.");
    }

    return staffProfile;
  }

  private static CustomerReviewView MapCustomerReview(Review review, Guid staffProfileId)
      => new(
          review.Id,
          review.AppointmentId,
          staffProfileId,
          review.Stars,
          review.Comment,
          review.CreatedAt);

  private static void ValidateCreateRequest(Guid appointmentId, ReviewCreateRequest request)
  {
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    if (appointmentId == Guid.Empty)
    {
      errors["appointmentId"] = ["Falta la cita."];
    }

    if (request.Stars is < 1 or > 5)
    {
      errors["stars"] = ["La calificación debe ser de 1 a 5 estrellas."];
    }

    if (request.Comment is not null && request.Comment.Length > 2000)
    {
      errors["comment"] = ["El comentario no puede superar 2000 caracteres."];
    }

    ThrowIfAnyErrors(errors);
  }

  private static void ThrowIfAnyErrors(Dictionary<string, string[]> errors)
  {
    if (errors.Count > 0)
    {
      throw new ValidationProblemException(errors);
    }
  }
}
