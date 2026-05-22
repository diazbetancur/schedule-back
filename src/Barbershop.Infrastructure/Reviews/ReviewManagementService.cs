using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Reviews;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Reviews;

internal sealed class ReviewManagementService : ICustomerReviewsService, IPublicReviewsService
{
  private readonly AppDbContext _dbContext;
  private readonly TimeProvider _timeProvider;

  public ReviewManagementService(AppDbContext dbContext, TimeProvider timeProvider)
  {
    _dbContext = dbContext;
    _timeProvider = timeProvider;
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
        ?? throw new KeyNotFoundException("The appointment was not found.");

    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    var isTerminalNonReviewable = appointment.Status is AppointmentStatus.Cancelled or AppointmentStatus.NoShow;
    var isPastEndTime = appointment.EndsAt <= nowUtc;

    if (isTerminalNonReviewable || (!isPastEndTime && appointment.Status is not AppointmentStatus.Completed))
    {
      errors["appointmentId"] = ["Only completed appointments can be reviewed."];
    }

    var hasExistingReview = await _dbContext.Reviews
        .AnyAsync(review => review.AppointmentId == appointmentId, cancellationToken);

    if (hasExistingReview)
    {
      errors["appointmentId"] = ["The appointment already has a review."];
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
          ["appointmentId"] = ["The appointment already has a review."]
        });
      }

      throw;
    }

    return MapCustomerReview(review, appointment.StaffProfileId);
  }

  async Task<IReadOnlyList<PublicStaffReviewView>> IPublicReviewsService.GetByStaffProfileAsync(Guid staffProfileId, CancellationToken cancellationToken)
  {
    _ = await LoadActiveStaffProfileAsync(staffProfileId, cancellationToken);

    var reviews = await _dbContext.Reviews
        .AsNoTracking()
        .Where(review => review.Appointment.StaffProfileId == staffProfileId && review.Appointment.Status == AppointmentStatus.Completed)
        .OrderByDescending(review => review.CreatedAt)
        .Select(review => new PublicStaffReviewView(
            review.AppointmentId,
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
        ?? throw new KeyNotFoundException("The current user was not found.");
  }

  private async Task<StaffProfile> LoadActiveStaffProfileAsync(Guid staffProfileId, CancellationToken cancellationToken)
  {
    var staffProfile = await _dbContext.StaffProfiles
        .Include(profile => profile.User)
        .AsNoTracking()
        .SingleOrDefaultAsync(profile => profile.Id == staffProfileId, cancellationToken)
        ?? throw new KeyNotFoundException("The staff profile was not found.");

    if (!staffProfile.IsActive || !staffProfile.User.IsActive)
    {
      throw new KeyNotFoundException("The staff profile was not found.");
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
      errors["appointmentId"] = ["AppointmentId is required."];
    }

    if (request.Stars is < 1 or > 5)
    {
      errors["stars"] = ["Stars must be between 1 and 5."];
    }

    if (request.Comment is not null && request.Comment.Length > 2000)
    {
      errors["comment"] = ["Comment must be 2000 characters or fewer."];
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
