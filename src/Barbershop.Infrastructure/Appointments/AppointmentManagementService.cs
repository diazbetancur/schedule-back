using System.Net.Mail;
using Barbershop.Application.Appointments;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Notifications;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Common;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Appointments;

internal sealed class AppointmentManagementService : ICustomerAppointmentsService, IStaffAppointmentsService, IAdminAppointmentsService
{
  private readonly AppDbContext _dbContext;
  private readonly TimeProvider _timeProvider;
  private readonly IAppointmentNotificationService _notificationService;

  public AppointmentManagementService(AppDbContext dbContext, TimeProvider timeProvider, IAppointmentNotificationService notificationService)
  {
    _dbContext = dbContext;
    _timeProvider = timeProvider;
    _notificationService = notificationService;
  }

  async Task<AppointmentView> ICustomerAppointmentsService.CreateAsync(
      Guid currentUserId,
      CustomerAppointmentCreateRequest request,
      CancellationToken cancellationToken)
  {
    ValidateCustomerCreateRequest(request);

    var currentUser = await LoadActiveUserAsync(currentUserId, cancellationToken);
    var staffProfile = await LoadActiveStaffProfileAsync(request.StaffProfileId, cancellationToken);

    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
    if (request.StartsAtUtc <= nowUtc)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["startsAtUtc"] = ["StartsAtUtc must be in the future."]
      });
    }

    var endsAtUtc = request.StartsAtUtc.AddMinutes(staffProfile.DefaultAppointmentDurationMinutes);

    await EnsureSlotIsAvailableAsync(staffProfile.Id, request.StartsAtUtc, endsAtUtc, null, cancellationToken);

    var appointment = new Appointment(
        staffProfile.Id,
        currentUser.FullName,
        request.StartsAtUtc,
        endsAtUtc,
        AppointmentStatus.Pending,
        AppointmentSource.CustomerBooking,
        nowUtc,
        currentUser.Id,
        currentUser.Email,
        currentUser.PhoneNumber,
        request.Notes);

    _dbContext.Appointments.Add(appointment);
    await _dbContext.SaveChangesAsync(cancellationToken);

    await _notificationService.NotifyStaffOfNewAppointmentAsync(
        new AppointmentNotificationContext(staffProfile.UserId, staffProfile.DisplayName, currentUser.Id, currentUser.FullName, appointment.StartsAt),
        cancellationToken);

    return Map(appointment);
  }

  async Task<IReadOnlyList<AppointmentView>> ICustomerAppointmentsService.GetHistoryAsync(Guid currentUserId, CancellationToken cancellationToken)
  {
    _ = await LoadActiveUserAsync(currentUserId, cancellationToken);

    var data = await _dbContext.Appointments
        .Where(a => a.CustomerUserId == currentUserId)
        .Join(
            _dbContext.StaffProfiles,
            a => a.StaffProfileId,
            sp => sp.Id,
            (a, sp) => new { Appointment = a, StaffName = sp.DisplayName })
        .OrderByDescending(x => x.Appointment.StartsAt)
        .ToListAsync(cancellationToken);

    return data.Select(x => Map(x.Appointment, x.StaffName)).ToArray();
  }

  async Task<AppointmentView> ICustomerAppointmentsService.CancelAsync(Guid currentUserId, Guid appointmentId, CancellationToken cancellationToken)
  {
    var appointment = await _dbContext.Appointments
        .Include(candidate => candidate.StaffProfile)
        .SingleOrDefaultAsync(
            candidate => candidate.Id == appointmentId && candidate.CustomerUserId == currentUserId,
            cancellationToken)
        ?? throw new KeyNotFoundException("The appointment was not found.");

    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    if (appointment.Status is not (AppointmentStatus.Pending or AppointmentStatus.Confirmed))
    {
      errors["appointmentId"] = ["Only pending or confirmed appointments can be cancelled."];
    }

    if (appointment.StartsAt <= nowUtc)
    {
      errors["appointmentId"] = ["Appointments can only be cancelled before the start time."];
    }

    ThrowIfAnyErrors(errors);

    appointment.UpdateStatus(AppointmentStatus.Cancelled, nowUtc);
    await _dbContext.SaveChangesAsync(cancellationToken);

    await _notificationService.NotifyStaffOfCustomerCancellationAsync(
        new AppointmentNotificationContext(appointment.StaffProfile.UserId, appointment.StaffProfile.DisplayName, appointment.CustomerUserId, appointment.CustomerName, appointment.StartsAt),
        cancellationToken);

    return Map(appointment);
  }

  async Task<IReadOnlyList<AppointmentView>> IStaffAppointmentsService.GetForCurrentStaffAsync(Guid currentUserId, CancellationToken cancellationToken)
  {
    var staffProfile = await LoadActiveStaffProfileByUserIdAsync(currentUserId, cancellationToken);
    return await LoadByStaffProfileAsync(staffProfile.Id, cancellationToken);
  }

  async Task<AppointmentView> IStaffAppointmentsService.CreateForCurrentStaffAsync(
      Guid currentUserId,
      StaffManualAppointmentCreateRequest request,
      CancellationToken cancellationToken)
  {
    var staffProfile = await LoadActiveStaffProfileByUserIdAsync(currentUserId, cancellationToken);
    var normalizedSchedule = NormalizeManualSchedule(request.StartsAtUtc, request.EndsAtUtc, staffProfile.DefaultAppointmentDurationMinutes);

    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
    if (normalizedSchedule.StartsAtUtc <= nowUtc)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["startsAtUtc"] = ["StartsAtUtc must be in the future."]
      });
    }

    ValidateManualSnapshot(request.CustomerName, request.CustomerEmail);

    await EnsureSlotIsAvailableAsync(staffProfile.Id, normalizedSchedule.StartsAtUtc, normalizedSchedule.EndsAtUtc, null, cancellationToken);

    var appointment = new Appointment(
        staffProfile.Id,
        request.CustomerName,
        normalizedSchedule.StartsAtUtc,
        normalizedSchedule.EndsAtUtc,
        AppointmentStatus.Confirmed,
        AppointmentSource.Manual,
        nowUtc,
        null,
        request.CustomerEmail,
        request.CustomerPhone,
        request.Notes);

    _dbContext.Appointments.Add(appointment);
    await _dbContext.SaveChangesAsync(cancellationToken);

    return Map(appointment);
  }

  async Task<AppointmentView> IStaffAppointmentsService.UpdateForCurrentStaffAsync(
      Guid currentUserId,
      Guid appointmentId,
      AppointmentUpdateRequest request,
      CancellationToken cancellationToken)
  {
    var staffProfile = await LoadActiveStaffProfileByUserIdAsync(currentUserId, cancellationToken);

    var appointment = await _dbContext.Appointments
        .SingleOrDefaultAsync(
            candidate => candidate.Id == appointmentId && candidate.StaffProfileId == staffProfile.Id,
            cancellationToken)
        ?? throw new KeyNotFoundException("The appointment was not found.");

    return await UpdateAppointmentCoreAsync(appointment, staffProfile, request, cancellationToken);
  }

  async Task<AppointmentView> IStaffAppointmentsService.UpdateStatusForCurrentStaffAsync(
      Guid currentUserId,
      Guid appointmentId,
      AppointmentStatusUpdateRequest request,
      CancellationToken cancellationToken)
  {
    var staffProfile = await LoadActiveStaffProfileByUserIdAsync(currentUserId, cancellationToken);

    var appointment = await _dbContext.Appointments
        .SingleOrDefaultAsync(
            candidate => candidate.Id == appointmentId && candidate.StaffProfileId == staffProfile.Id,
            cancellationToken)
        ?? throw new KeyNotFoundException("The appointment was not found.");

    return await UpdateStatusCoreAsync(appointment, staffProfile, request, cancellationToken);
  }

  async Task<IReadOnlyList<AppointmentView>> IAdminAppointmentsService.GetAsync(Guid? staffProfileId, CancellationToken cancellationToken)
  {
    if (staffProfileId is null)
    {
      var all = await _dbContext.Appointments
          .OrderByDescending(appointment => appointment.StartsAt)
          .ToListAsync(cancellationToken);

      return all.Select(a => Map(a)).ToArray();
    }

    _ = await LoadActiveStaffProfileAsync(staffProfileId.Value, cancellationToken);
    return await LoadByStaffProfileAsync(staffProfileId.Value, cancellationToken);
  }

  async Task<AppointmentView> IAdminAppointmentsService.CreateAsync(AdminManualAppointmentCreateRequest request, CancellationToken cancellationToken)
  {
    if (request.StaffProfileId == Guid.Empty)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["staffProfileId"] = ["StaffProfileId is required."]
      });
    }

    var staffProfile = await LoadActiveStaffProfileAsync(request.StaffProfileId, cancellationToken);
    var normalizedSchedule = NormalizeManualSchedule(request.StartsAtUtc, request.EndsAtUtc, staffProfile.DefaultAppointmentDurationMinutes);

    ValidateManualSnapshot(request.CustomerName, request.CustomerEmail);
    await EnsureSlotIsAvailableAsync(staffProfile.Id, normalizedSchedule.StartsAtUtc, normalizedSchedule.EndsAtUtc, null, cancellationToken);

    var appointment = new Appointment(
        staffProfile.Id,
        request.CustomerName,
        normalizedSchedule.StartsAtUtc,
        normalizedSchedule.EndsAtUtc,
        AppointmentStatus.Confirmed,
        AppointmentSource.Manual,
        _timeProvider.GetUtcNow().UtcDateTime,
        null,
        request.CustomerEmail,
        request.CustomerPhone,
        request.Notes);

    _dbContext.Appointments.Add(appointment);
    await _dbContext.SaveChangesAsync(cancellationToken);

    await _notificationService.NotifyStaffOfNewAppointmentAsync(
        new AppointmentNotificationContext(staffProfile.UserId, staffProfile.DisplayName, null, appointment.CustomerName, appointment.StartsAt),
        cancellationToken);

    return Map(appointment);
  }

  async Task<AppointmentView> IAdminAppointmentsService.UpdateAsync(Guid appointmentId, AppointmentUpdateRequest request, CancellationToken cancellationToken)
  {
    var appointment = await _dbContext.Appointments
        .SingleOrDefaultAsync(candidate => candidate.Id == appointmentId, cancellationToken)
        ?? throw new KeyNotFoundException("The appointment was not found.");

    var staffProfile = await LoadActiveStaffProfileAsync(appointment.StaffProfileId, cancellationToken);

    return await UpdateAppointmentCoreAsync(appointment, staffProfile, request, cancellationToken);
  }

  async Task<AppointmentView> IAdminAppointmentsService.UpdateStatusAsync(
      Guid appointmentId,
      AppointmentStatusUpdateRequest request,
      CancellationToken cancellationToken)
  {
    var appointment = await _dbContext.Appointments
        .SingleOrDefaultAsync(candidate => candidate.Id == appointmentId, cancellationToken)
        ?? throw new KeyNotFoundException("The appointment was not found.");

    var staffProfile = await LoadActiveStaffProfileAsync(appointment.StaffProfileId, cancellationToken);

    return await UpdateStatusCoreAsync(appointment, staffProfile, request, cancellationToken);
  }

  private async Task<IReadOnlyList<AppointmentView>> LoadByStaffProfileAsync(Guid staffProfileId, CancellationToken cancellationToken)
  {
    var appointments = await _dbContext.Appointments
        .Where(appointment => appointment.StaffProfileId == staffProfileId)
        .OrderByDescending(appointment => appointment.StartsAt)
        .ToListAsync(cancellationToken);

    return appointments.Select(a => Map(a)).ToArray();
  }

  private async Task<AppointmentView> UpdateAppointmentCoreAsync(
      Appointment appointment,
      StaffProfile staffProfile,
      AppointmentUpdateRequest request,
      CancellationToken cancellationToken)
  {
    ValidateUpdateRequest(request);

    if (appointment.Status is not (AppointmentStatus.Pending or AppointmentStatus.Confirmed))
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["status"] = ["Only pending or confirmed appointments can be edited."]
      });
    }

    await EnsureSlotIsAvailableAsync(appointment.StaffProfileId, request.StartsAtUtc, request.EndsAtUtc, appointment.Id, cancellationToken);

    appointment.UpdateDetails(
        request.CustomerName,
        request.CustomerEmail,
        request.CustomerPhone,
        request.StartsAtUtc,
        request.EndsAtUtc,
        request.Notes,
        _timeProvider.GetUtcNow().UtcDateTime);

    await _dbContext.SaveChangesAsync(cancellationToken);

    await _notificationService.NotifyCustomerOfAppointmentUpdateAsync(
        new AppointmentNotificationContext(staffProfile.UserId, staffProfile.DisplayName, appointment.CustomerUserId, appointment.CustomerName, appointment.StartsAt),
        cancellationToken);

    return Map(appointment);
  }

  private async Task<AppointmentView> UpdateStatusCoreAsync(
      Appointment appointment,
      StaffProfile staffProfile,
      AppointmentStatusUpdateRequest request,
      CancellationToken cancellationToken)
  {
    var allowedStatuses = new[]
    {
            AppointmentStatus.Confirmed,
            AppointmentStatus.Completed,
            AppointmentStatus.Cancelled,
            AppointmentStatus.NoShow
        };

    if (!allowedStatuses.Contains(request.Status))
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["status"] = ["Status must be Confirmed, Completed, Cancelled, or NoShow."]
      });
    }

    if (appointment.Status == request.Status)
    {
      return Map(appointment);
    }

    if (appointment.Status is AppointmentStatus.Completed or AppointmentStatus.Cancelled or AppointmentStatus.NoShow)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["status"] = ["A terminal-status appointment cannot be transitioned again."]
      });
    }

    appointment.UpdateStatus(request.Status, _timeProvider.GetUtcNow().UtcDateTime);
    await _dbContext.SaveChangesAsync(cancellationToken);

    if (request.Status == AppointmentStatus.Cancelled)
    {
      await _notificationService.NotifyCustomerOfAppointmentCancellationAsync(
          new AppointmentNotificationContext(staffProfile.UserId, staffProfile.DisplayName, appointment.CustomerUserId, appointment.CustomerName, appointment.StartsAt),
          cancellationToken);
    }

    if (request.Status == AppointmentStatus.Confirmed)
    {
      await _notificationService.NotifyCustomerOfAppointmentConfirmationAsync(
          new AppointmentNotificationContext(staffProfile.UserId, staffProfile.DisplayName, appointment.CustomerUserId, appointment.CustomerName, appointment.StartsAt),
          cancellationToken);
    }

    return Map(appointment);
  }

  private async Task<User> LoadActiveUserAsync(Guid userId, CancellationToken cancellationToken)
  {
    return await _dbContext.Users
        .SingleOrDefaultAsync(user => user.Id == userId && user.IsActive, cancellationToken)
        ?? throw new KeyNotFoundException("The current user was not found.");
  }

  private async Task<StaffProfile> LoadActiveStaffProfileAsync(Guid staffProfileId, CancellationToken cancellationToken)
  {
    var staffProfile = await _dbContext.StaffProfiles
        .Include(profile => profile.User)
        .SingleOrDefaultAsync(profile => profile.Id == staffProfileId, cancellationToken)
        ?? throw new KeyNotFoundException("The staff profile was not found.");

    if (!staffProfile.IsActive || !staffProfile.User.IsActive)
    {
      throw new KeyNotFoundException("The staff profile was not found.");
    }

    return staffProfile;
  }

  private async Task<StaffProfile> LoadActiveStaffProfileByUserIdAsync(Guid userId, CancellationToken cancellationToken)
  {
    var staffProfile = await _dbContext.StaffProfiles
        .Include(profile => profile.User)
        .SingleOrDefaultAsync(profile => profile.UserId == userId, cancellationToken)
        ?? throw new KeyNotFoundException("The staff profile was not found for the current user.");

    if (!staffProfile.IsActive || !staffProfile.User.IsActive)
    {
      throw new KeyNotFoundException("The staff profile was not found for the current user.");
    }

    return staffProfile;
  }

  private async Task EnsureSlotIsAvailableAsync(
      Guid staffProfileId,
      DateTime startsAtUtc,
      DateTime endsAtUtc,
      Guid? excludedAppointmentId,
      CancellationToken cancellationToken)
  {
    // Availability rules are stored as Bogota wall-clock TimeOnly values, so the UTC
    // instant must be converted to local time before comparing against them.
    var startsAtLocal = BogotaClock.ToLocal(startsAtUtc);
    var endsAtLocal = BogotaClock.ToLocal(endsAtUtc);

    if (startsAtLocal.Date != endsAtLocal.Date)
    {
      throw new ConflictException("The selected slot is not available.");
    }

    var startTime = TimeOnly.FromDateTime(startsAtLocal);
    var endTime = TimeOnly.FromDateTime(endsAtLocal);

    var coveredByRule = await _dbContext.StaffAvailabilityRules
        .AnyAsync(
            rule => rule.StaffProfileId == staffProfileId
                && rule.IsActive
                && rule.DayOfWeek == startsAtLocal.DayOfWeek
                && rule.StartTime <= startTime
                && endTime <= rule.EndTime,
            cancellationToken);

    if (!coveredByRule)
    {
      throw new ConflictException("The selected slot is not available.");
    }

    var overlapsUnavailablePeriod = await _dbContext.StaffUnavailablePeriods
        .AnyAsync(
            period => period.StaffProfileId == staffProfileId
                && period.StartsAt < endsAtUtc
                && startsAtUtc < period.EndsAt,
            cancellationToken);

    if (overlapsUnavailablePeriod)
    {
      throw new ConflictException("The selected slot is not available.");
    }

    var overlapsAppointment = await _dbContext.Appointments
        .AnyAsync(
            appointment => appointment.StaffProfileId == staffProfileId
                && (appointment.Status == AppointmentStatus.Pending || appointment.Status == AppointmentStatus.Confirmed)
                && appointment.StartsAt < endsAtUtc
                && startsAtUtc < appointment.EndsAt
                && (!excludedAppointmentId.HasValue || appointment.Id != excludedAppointmentId.Value),
            cancellationToken);

    if (overlapsAppointment)
    {
      throw new ConflictException("The selected slot is not available.");
    }
  }

  private static (DateTime StartsAtUtc, DateTime EndsAtUtc) NormalizeManualSchedule(DateTime startsAtUtc, DateTime? endsAtUtc, int defaultDurationMinutes)
  {
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    if (startsAtUtc == default)
    {
      errors["startsAtUtc"] = ["StartsAtUtc is required."];
    }
    else if (startsAtUtc.Kind != DateTimeKind.Utc)
    {
      errors["startsAtUtc"] = ["StartsAtUtc must be provided in UTC."];
    }

    if (endsAtUtc.HasValue && endsAtUtc.Value.Kind != DateTimeKind.Utc)
    {
      errors["endsAtUtc"] = ["EndsAtUtc must be provided in UTC."];
    }

    if (errors.Count > 0)
    {
      throw new ValidationProblemException(errors);
    }

    var normalizedEndsAtUtc = endsAtUtc ?? startsAtUtc.AddMinutes(defaultDurationMinutes);
    if (normalizedEndsAtUtc <= startsAtUtc)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["endsAtUtc"] = ["EndsAtUtc must be after StartsAtUtc."]
      });
    }

    return (startsAtUtc, normalizedEndsAtUtc);
  }

  private static void ValidateCustomerCreateRequest(CustomerAppointmentCreateRequest request)
  {
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    if (request.StaffProfileId == Guid.Empty)
    {
      errors["staffProfileId"] = ["StaffProfileId is required."];
    }

    if (request.StartsAtUtc == default)
    {
      errors["startsAtUtc"] = ["StartsAtUtc is required."];
    }
    else if (request.StartsAtUtc.Kind != DateTimeKind.Utc)
    {
      errors["startsAtUtc"] = ["StartsAtUtc must be provided in UTC."];
    }

    ThrowIfAnyErrors(errors);
  }

  private static void ValidateUpdateRequest(AppointmentUpdateRequest request)
  {
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    if (request.StartsAtUtc == default)
    {
      errors["startsAtUtc"] = ["StartsAtUtc is required."];
    }
    else if (request.StartsAtUtc.Kind != DateTimeKind.Utc)
    {
      errors["startsAtUtc"] = ["StartsAtUtc must be provided in UTC."];
    }

    if (request.EndsAtUtc == default)
    {
      errors["endsAtUtc"] = ["EndsAtUtc is required."];
    }
    else if (request.EndsAtUtc.Kind != DateTimeKind.Utc)
    {
      errors["endsAtUtc"] = ["EndsAtUtc must be provided in UTC."];
    }

    if (request.EndsAtUtc <= request.StartsAtUtc)
    {
      errors["endsAtUtc"] = ["EndsAtUtc must be after StartsAtUtc."];
    }

    if (string.IsNullOrWhiteSpace(request.CustomerName))
    {
      errors["customerName"] = ["CustomerName is required."];
    }

    if (!string.IsNullOrWhiteSpace(request.CustomerEmail) && !IsValidEmail(request.CustomerEmail))
    {
      errors["customerEmail"] = ["CustomerEmail must be a valid email address."];
    }

    ThrowIfAnyErrors(errors);
  }

  private static void ValidateManualSnapshot(string customerName, string? customerEmail)
  {
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    if (string.IsNullOrWhiteSpace(customerName))
    {
      errors["customerName"] = ["CustomerName is required."];
    }

    if (!string.IsNullOrWhiteSpace(customerEmail) && !IsValidEmail(customerEmail))
    {
      errors["customerEmail"] = ["CustomerEmail must be a valid email address."];
    }

    ThrowIfAnyErrors(errors);
  }

  private static bool IsValidEmail(string value)
  {
    try
    {
      _ = new MailAddress(value);
      return true;
    }
    catch (FormatException)
    {
      return false;
    }
  }

  private static AppointmentView Map(Appointment appointment, string? staffName = null)
      => new(
          appointment.Id,
          appointment.StaffProfileId,
          appointment.CustomerUserId,
          appointment.CustomerName,
          appointment.CustomerEmail,
          appointment.CustomerPhone,
          appointment.StartsAt,
          appointment.EndsAt,
          appointment.Status,
          appointment.Source,
          appointment.Notes,
          appointment.CreatedAt,
          appointment.UpdatedAt,
          staffName);

  private static void ThrowIfAnyErrors(Dictionary<string, string[]> errors)
  {
    if (errors.Count > 0)
    {
      throw new ValidationProblemException(errors);
    }
  }
}
