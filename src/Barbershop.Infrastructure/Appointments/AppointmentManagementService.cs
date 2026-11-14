using System.Net.Mail;
using Barbershop.Application.Appointments;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Common.Paging;
using Barbershop.Application.Notifications;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Common;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Availability;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Staff;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Appointments;

/// <summary>
/// Agenda of the professionals. Since ADR-0016 B3 every new appointment is for one general service the professional
/// offers and reserves its duration (D7, D10, D11). Every change to an agenda (booking, rescheduling, manual creation
/// or edition) runs in a transaction that first locks the professional's row (<c>FOR UPDATE</c>) and then checks
/// overlaps, so two simultaneous changes of the same agenda run one after the other and never both take the time (T7).
/// Notifications are sent after the commit.
/// </summary>
internal sealed class AppointmentManagementService : ICustomerAppointmentsService, IStaffAppointmentsService, IAdminAppointmentsService
{
  private const string SlotNotAvailableMessage = "Ese horario ya no está disponible. Elige otro.";
  private const string ServiceNotOfferedMessage = "El profesional no ofrece este servicio en este momento. Elige otro servicio.";
  private const string StaffCannotChangeDurationMessage = "Solo administración puede cambiar la duración de la cita.";

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
    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
    if (request.StartsAtUtc <= nowUtc)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["startsAtUtc"] = ["La cita debe ser en una fecha y hora futuras."]
      });
    }

    Appointment appointment;
    StaffProfile staffProfile;
    await using (var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken))
    {
      await _dbContext.LockStaffProfileAsync(request.StaffProfileId, cancellationToken);
      staffProfile = await LoadActiveStaffProfileAsync(request.StaffProfileId, cancellationToken);

      // Revalidated under the lock: the offering, its service and category must still be active.
      var service = await ResolveOfferingAsync(staffProfile.Id, request.GeneralServiceId, cancellationToken);
      var endsAtUtc = request.StartsAtUtc.AddMinutes(service.DurationMinutes);
      await EnsureSlotIsAvailableAsync(staffProfile.Id, request.StartsAtUtc, endsAtUtc, null, requireGrid: true, cancellationToken);

      appointment = Appointment.ForService(
          staffProfile.Id,
          currentUser.FullName,
          request.StartsAtUtc,
          service,
          AppointmentStatus.Pending,
          AppointmentSource.CustomerBooking,
          nowUtc,
          currentUser.Id,
          currentUser.Email,
          currentUser.PhoneNumber,
          request.Notes);

      _dbContext.Appointments.Add(appointment);
      await _dbContext.SaveChangesAsync(cancellationToken);
      await transaction.CommitAsync(cancellationToken);
    }

    await _notificationService.NotifyStaffOfNewAppointmentAsync(
        new AppointmentNotificationContext(staffProfile.UserId, staffProfile.DisplayName, currentUser.Id, currentUser.FullName, appointment.StartsAt, appointment.Id),
        cancellationToken);

    return Map(appointment);
  }

  async Task<PagedResult<AppointmentView>> ICustomerAppointmentsService.GetHistoryAsync(
      Guid currentUserId, CustomerAppointmentListQuery query, CancellationToken cancellationToken)
  {
    var page = PageRequest.Create(query.Page, query.PageSize);
    var scope = ListFilters.ParseOption(query.Scope, "scope", CustomerAppointmentScopes.Upcoming, CustomerAppointmentScopes.Past);
    var status = ListFilters.ParseEnumList<AppointmentStatus>(query.Status, "status");
    // Upcoming is the queue still to attend (no default range); the rest is history (30-day windows).
    var range = scope == CustomerAppointmentScopes.Upcoming
        ? HistoryRange.ResolveOptional(query.From, query.To)
        : HistoryRange.Resolve(query.From, query.To, TodayInBogota());

    _ = await LoadActiveUserAsync(currentUserId, cancellationToken);

    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
    var appointments = FilterByStartDate(
        _dbContext.Appointments.AsNoTracking().Where(a => a.CustomerUserId == currentUserId), range?.From, range?.To);
    if (status is { } wanted)
    {
      appointments = appointments.Where(a => wanted.Contains(a.Status));
    }

    // "Upcoming" is exactly what the customer can still attend; "Past" is its complement.
    if (scope == CustomerAppointmentScopes.Upcoming)
    {
      appointments = appointments.Where(a =>
          a.StartsAt > nowUtc && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed));
    }
    else if (scope == CustomerAppointmentScopes.Past)
    {
      appointments = appointments.Where(a =>
          !(a.StartsAt > nowUtc && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed)));
    }

    var joined = appointments.Join(
        _dbContext.StaffProfiles,
        a => a.StaffProfileId,
        sp => sp.Id,
        (a, sp) => new { Appointment = a, StaffName = sp.DisplayName });

    // Upcoming reads soonest first (what comes next); every other view reads newest first.
    var ordered = scope == CustomerAppointmentScopes.Upcoming
        ? joined.OrderBy(x => x.Appointment.StartsAt).ThenBy(x => x.Appointment.Id)
        : joined.OrderByDescending(x => x.Appointment.StartsAt).ThenByDescending(x => x.Appointment.Id);

    return await ordered.ToPagedResultAsync(page, x => Map(x.Appointment, x.StaffName), cancellationToken);
  }

  async Task<AppointmentView> ICustomerAppointmentsService.CancelAsync(Guid currentUserId, Guid appointmentId, CancellationToken cancellationToken)
  {
    var appointment = await _dbContext.Appointments
        .Include(candidate => candidate.StaffProfile)
        .SingleOrDefaultAsync(
            candidate => candidate.Id == appointmentId && candidate.CustomerUserId == currentUserId,
            cancellationToken)
        ?? throw new KeyNotFoundException("No encontramos la cita.");

    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    if (appointment.Status is not (AppointmentStatus.Pending or AppointmentStatus.Confirmed))
    {
      errors["appointmentId"] = ["Esta cita ya no se puede cancelar: solo se cancelan citas pendientes o confirmadas."];
    }

    if (appointment.StartsAt <= nowUtc)
    {
      errors["appointmentId"] = ["La cita solo se puede cancelar antes de su hora de inicio."];
    }

    ThrowIfAnyErrors(errors);

    appointment.UpdateStatus(AppointmentStatus.Cancelled, nowUtc);
    await _dbContext.SaveChangesAsync(cancellationToken);

    await _notificationService.NotifyStaffOfCustomerCancellationAsync(
        new AppointmentNotificationContext(appointment.StaffProfile.UserId, appointment.StaffProfile.DisplayName, appointment.CustomerUserId, appointment.CustomerName, appointment.StartsAt, appointment.Id),
        cancellationToken);

    return Map(appointment);
  }

  async Task<AppointmentView> ICustomerAppointmentsService.RescheduleAsync(
      Guid currentUserId,
      Guid appointmentId,
      CustomerAppointmentRescheduleRequest request,
      CancellationToken cancellationToken)
  {
    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
    var requestErrors = new Dictionary<string, string[]>(StringComparer.Ordinal);
    if (request.StartsAtUtc == default)
    {
      requestErrors["startsAtUtc"] = ["Indica la fecha y la hora de la cita."];
    }
    else if (request.StartsAtUtc.Kind != DateTimeKind.Utc)
    {
      requestErrors["startsAtUtc"] = ["La fecha y la hora de la cita no tienen el formato esperado."];
    }

    var staffProfileId = await _dbContext.Appointments.AsNoTracking()
        .Where(candidate => candidate.Id == appointmentId && candidate.CustomerUserId == currentUserId)
        .Select(candidate => (Guid?)candidate.StaffProfileId)
        .SingleOrDefaultAsync(cancellationToken)
        ?? throw new KeyNotFoundException("No encontramos la cita.");

    Appointment appointment;
    await using (var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken))
    {
      // The professional of an appointment never changes, so the lock taken from the read above is the right one.
      await _dbContext.LockStaffProfileAsync(staffProfileId, cancellationToken);
      appointment = await _dbContext.Appointments
          .Include(candidate => candidate.StaffProfile)
          .SingleOrDefaultAsync(
              candidate => candidate.Id == appointmentId && candidate.CustomerUserId == currentUserId,
              cancellationToken)
          ?? throw new KeyNotFoundException("No encontramos la cita.");

      var errors = new Dictionary<string, string[]>(requestErrors, StringComparer.Ordinal);
      if (appointment.Status is not (AppointmentStatus.Pending or AppointmentStatus.Confirmed))
      {
        errors["appointmentId"] = ["Esta cita ya no se puede reprogramar: fue cancelada, completada o marcada como no asistida."];
      }
      else if (appointment.StartsAt <= nowUtc)
      {
        errors["appointmentId"] = ["La cita solo se puede reprogramar antes de su hora de inicio."];
      }

      if (!errors.ContainsKey("startsAtUtc") && request.StartsAtUtc <= nowUtc)
      {
        errors["startsAtUtc"] = ["La cita debe ser en una fecha y hora futuras."];
      }

      ThrowIfAnyErrors(errors);

      if (request.StartsAtUtc == appointment.StartsAt)
      {
        return Map(appointment, appointment.StaffProfile.DisplayName);
      }

      if (!appointment.StaffProfile.IsActive)
      {
        throw new ConflictException(SlotNotAvailableMessage);
      }

      // Same barber, same service and same reserved length (D11): the current offering is not read again.
      var endsAtUtc = request.StartsAtUtc + (appointment.EndsAt - appointment.StartsAt);
      await EnsureSlotIsAvailableAsync(appointment.StaffProfileId, request.StartsAtUtc, endsAtUtc, appointment.Id, requireGrid: true, cancellationToken);

      appointment.Reschedule(request.StartsAtUtc, nowUtc);

      // The barber has not agreed to the new time yet: it goes back to pending, like a new booking.
      if (appointment.Status == AppointmentStatus.Confirmed)
      {
        appointment.UpdateStatus(AppointmentStatus.Pending, nowUtc);
      }

      await _dbContext.SaveChangesAsync(cancellationToken);
      await transaction.CommitAsync(cancellationToken);
    }

    await _notificationService.NotifyStaffOfAppointmentRescheduledAsync(
        new AppointmentNotificationContext(appointment.StaffProfile.UserId, appointment.StaffProfile.DisplayName, appointment.CustomerUserId, appointment.CustomerName, appointment.StartsAt, appointment.Id),
        cancellationToken);

    return Map(appointment, appointment.StaffProfile.DisplayName);
  }

  async Task<PagedResult<AppointmentView>> IStaffAppointmentsService.GetForCurrentStaffAsync(
      Guid currentUserId, StaffAppointmentListQuery query, CancellationToken cancellationToken)
  {
    var page = PageRequest.Create(query.Page, query.PageSize);
    var ascending = ParseSort(query.Sort, query.Scope);
    var status = ListFilters.ParseEnumList<AppointmentStatus>(query.Status, "status");
    var (from, to) = ResolveListWindow(query.Scope, query.From, query.To);

    var staffProfile = await LoadActiveStaffProfileByUserIdAsync(currentUserId, cancellationToken);
    var appointments = FilterByStartDate(
        _dbContext.Appointments.AsNoTracking().Where(appointment => appointment.StaffProfileId == staffProfile.Id),
        from,
        to);
    if (status is { } wanted)
    {
      appointments = appointments.Where(appointment => wanted.Contains(appointment.Status));
    }

    return await OrderByStart(appointments, ascending)
        .ToPagedResultAsync(page, appointment => Map(appointment), cancellationToken);
  }

  async Task<IReadOnlyList<AppointmentServiceOption>> IStaffAppointmentsService.GetServiceOptionsForCurrentStaffAsync(
      Guid currentUserId, CancellationToken cancellationToken)
  {
    var staffProfile = await LoadActiveStaffProfileByUserIdAsync(currentUserId, cancellationToken);
    return await LoadServiceOptionsAsync(staffProfile.Id, cancellationToken);
  }

  async Task<AppointmentView> IStaffAppointmentsService.CreateForCurrentStaffAsync(
      Guid currentUserId,
      StaffManualAppointmentCreateRequest request,
      CancellationToken cancellationToken)
  {
    var staffProfileId = (await LoadActiveStaffProfileByUserIdAsync(currentUserId, cancellationToken)).Id;
    ValidateManualCreate(request.StartsAtUtc, request.GeneralServiceId, request.DurationMinutes);

    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
    if (request.StartsAtUtc <= nowUtc)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["startsAtUtc"] = ["La cita debe ser en una fecha y hora futuras."]
      });
    }

    ValidateManualSnapshot(request.CustomerName, request.CustomerEmail, request.CustomerPhone, request.Notes);

    return await CreateManualAsync(
        staffProfileId,
        request.StartsAtUtc,
        request.GeneralServiceId,
        request.DurationMinutes,
        canChangeDuration: false,
        request.CustomerName,
        request.CustomerEmail,
        request.CustomerPhone,
        request.Notes,
        cancellationToken);
  }

  async Task<AppointmentView> IStaffAppointmentsService.UpdateForCurrentStaffAsync(
      Guid currentUserId,
      Guid appointmentId,
      AppointmentUpdateRequest request,
      CancellationToken cancellationToken)
  {
    var staffProfile = await LoadActiveStaffProfileByUserIdAsync(currentUserId, cancellationToken);
    return await UpdateAppointmentCoreAsync(
        appointmentId, staffProfile.Id, request, canChangeDuration: false, cancellationToken);
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
        ?? throw new KeyNotFoundException("No encontramos la cita.");

    return await UpdateStatusCoreAsync(appointment, staffProfile, request, cancellationToken);
  }

  async Task<PagedResult<AppointmentView>> IAdminAppointmentsService.GetAsync(
      AdminAppointmentListQuery query, CancellationToken cancellationToken)
  {
    var page = PageRequest.Create(query.Page, query.PageSize);
    var ascending = ParseSort(query.Sort, query.Scope);
    var status = ListFilters.ParseEnumList<AppointmentStatus>(query.Status, "status");
    var (from, to) = ResolveListWindow(query.Scope, query.From, query.To);

    var appointments = FilterByStartDate(_dbContext.Appointments.AsNoTracking(), from, to);

    if (query.StaffProfileId is { } staffProfileId)
    {
      // History can be filtered by a professional who is no longer active; only an unknown id is an error.
      if (!await _dbContext.StaffProfiles.AnyAsync(profile => profile.Id == staffProfileId, cancellationToken))
      {
        throw new KeyNotFoundException("No encontramos este profesional.");
      }

      appointments = appointments.Where(appointment => appointment.StaffProfileId == staffProfileId);
    }

    if (status is { } wanted)
    {
      appointments = appointments.Where(appointment => wanted.Contains(appointment.Status));
    }

    if (query.GeneralServiceId is { } generalServiceId)
    {
      appointments = appointments.Where(appointment => appointment.GeneralServiceId == generalServiceId);
    }

    if (ListFilters.Text(query.Customer) is { } customer)
    {
      var upper = customer.ToUpperInvariant();
      appointments = appointments.Where(appointment =>
          appointment.CustomerName.ToUpper().Contains(upper)
          || (appointment.CustomerEmail != null && appointment.CustomerEmail.ToUpper().Contains(upper))
          || (appointment.CustomerPhone != null && appointment.CustomerPhone.Contains(customer)));
    }

    return await OrderByStart(appointments, ascending)
        .ToPagedResultAsync(page, appointment => Map(appointment), cancellationToken);
  }

  async Task<IReadOnlyList<AppointmentServiceOption>> IAdminAppointmentsService.GetServiceOptionsAsync(
      Guid staffProfileId, CancellationToken cancellationToken)
  {
    _ = await LoadActiveStaffProfileAsync(staffProfileId, cancellationToken);
    return await LoadServiceOptionsAsync(staffProfileId, cancellationToken);
  }

  async Task<AppointmentView> IAdminAppointmentsService.CreateAsync(AdminManualAppointmentCreateRequest request, CancellationToken cancellationToken)
  {
    if (request.StaffProfileId == Guid.Empty)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["staffProfileId"] = ["Elige un profesional."]
      });
    }

    ValidateManualCreate(request.StartsAtUtc, request.GeneralServiceId, request.DurationMinutes);
    ValidateManualSnapshot(request.CustomerName, request.CustomerEmail, request.CustomerPhone, request.Notes);

    return await CreateManualAsync(
        request.StaffProfileId,
        request.StartsAtUtc,
        request.GeneralServiceId,
        request.DurationMinutes,
        canChangeDuration: true,
        request.CustomerName,
        request.CustomerEmail,
        request.CustomerPhone,
        request.Notes,
        cancellationToken);
  }

  async Task<AppointmentView> IAdminAppointmentsService.UpdateAsync(Guid appointmentId, AppointmentUpdateRequest request, CancellationToken cancellationToken)
  {
    var staffProfileId = await _dbContext.Appointments.AsNoTracking()
        .Where(candidate => candidate.Id == appointmentId)
        .Select(candidate => (Guid?)candidate.StaffProfileId)
        .SingleOrDefaultAsync(cancellationToken)
        ?? throw new KeyNotFoundException("No encontramos la cita.");

    return await UpdateAppointmentCoreAsync(appointmentId, staffProfileId, request, canChangeDuration: true, cancellationToken);
  }

  async Task<AppointmentView> IAdminAppointmentsService.UpdateStatusAsync(
      Guid appointmentId,
      AppointmentStatusUpdateRequest request,
      CancellationToken cancellationToken)
  {
    var appointment = await _dbContext.Appointments
        .SingleOrDefaultAsync(candidate => candidate.Id == appointmentId, cancellationToken)
        ?? throw new KeyNotFoundException("No encontramos la cita.");

    // BUG-114: administration must still be able to cancel or close the appointments of a deactivated professional
    // (the customer is notified as usual), so the profile is loaded whatever its state.
    var staffProfile = await LoadStaffProfileAnyStateAsync(appointment.StaffProfileId, cancellationToken);

    return await UpdateStatusCoreAsync(appointment, staffProfile, request, cancellationToken);
  }

  private async Task<StaffProfile> LoadStaffProfileAnyStateAsync(Guid staffProfileId, CancellationToken cancellationToken)
      => await _dbContext.StaffProfiles
          .Include(profile => profile.User)
          .SingleOrDefaultAsync(profile => profile.Id == staffProfileId, cancellationToken)
          ?? throw new KeyNotFoundException("No encontramos este profesional.");

  private async Task<AppointmentView> CreateManualAsync(
      Guid staffProfileId,
      DateTime startsAtUtc,
      Guid generalServiceId,
      int? durationMinutes,
      bool canChangeDuration,
      string customerName,
      string? customerEmail,
      string? customerPhone,
      string? notes,
      CancellationToken cancellationToken)
  {
    Appointment appointment;
    StaffProfile staffProfile;
    await using (var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken))
    {
      await _dbContext.LockStaffProfileAsync(staffProfileId, cancellationToken);
      staffProfile = await LoadActiveStaffProfileAsync(staffProfileId, cancellationToken);

      // F3: the service must be one the professional offers; the duration is the offering's unless administration
      // sets another one for this appointment.
      var offered = await ResolveOfferingAsync(staffProfile.Id, generalServiceId, cancellationToken);
      var duration = ResolveDuration(offered.DurationMinutes, durationMinutes, canChangeDuration);
      var service = offered with { DurationMinutes = duration };
      await EnsureSlotIsAvailableAsync(
          staffProfile.Id, startsAtUtc, startsAtUtc.AddMinutes(duration), null, requireGrid: false, cancellationToken);

      appointment = Appointment.ForService(
          staffProfile.Id,
          customerName,
          startsAtUtc,
          service,
          AppointmentStatus.Confirmed,
          AppointmentSource.Manual,
          _timeProvider.GetUtcNow().UtcDateTime,
          null,
          customerEmail,
          customerPhone,
          notes);

      _dbContext.Appointments.Add(appointment);
      await _dbContext.SaveChangesAsync(cancellationToken);
      await transaction.CommitAsync(cancellationToken);
    }

    await _notificationService.NotifyStaffOfNewAppointmentAsync(
        new AppointmentNotificationContext(staffProfile.UserId, staffProfile.DisplayName, null, appointment.CustomerName, appointment.StartsAt, appointment.Id),
        cancellationToken);

    return Map(appointment);
  }

  /// <summary>
  /// Agenda: from today on (an earlier <c>from</c> is raised to today), any length. History (default): a
  /// <see cref="HistoryRange"/> window, so the limit cannot be bypassed by omitting the scope.
  /// </summary>
  private (DateOnly? From, DateOnly? To) ResolveListWindow(string? scope, DateOnly? from, DateOnly? to)
  {
    var parsed = ListFilters.ParseOption(scope, "scope", AppointmentListScopes.Agenda, AppointmentListScopes.History);
    var today = TodayInBogota();
    if (parsed == AppointmentListScopes.Agenda)
    {
      var start = from is { } requested && requested > today ? requested : today;
      ListFilters.ValidateRange(start, to);
      return (start, to);
    }

    var range = HistoryRange.Resolve(from, to, today);
    return (range.From, range.To);
  }

  private DateOnly TodayInBogota()
      => DateOnly.FromDateTime(BogotaClock.ToLocal(_timeProvider.GetUtcNow().UtcDateTime));

  /// <summary>
  /// An explicit <c>sort</c> wins. Without one, the agenda (the queue still to attend) is soonest first and the history
  /// newest first (SONDA BUG-022: an empty value used to fall through to descending for the agenda too).
  /// </summary>
  private static bool ParseSort(string? sort, string? scope)
  {
    var parsed = ListFilters.ParseOption(sort, "sort", AppointmentListSort.Asc, AppointmentListSort.Desc);
    if (parsed is not null)
    {
      return parsed == AppointmentListSort.Asc;
    }

    return ListFilters.ParseOption(scope, "scope", AppointmentListScopes.Agenda, AppointmentListScopes.History)
        == AppointmentListScopes.Agenda;
  }

  /// <summary>A total order: start, then id, both in the same direction.</summary>
  private static IQueryable<Appointment> OrderByStart(IQueryable<Appointment> appointments, bool ascending)
      => ascending
          ? appointments.OrderBy(appointment => appointment.StartsAt).ThenBy(appointment => appointment.Id)
          : appointments.OrderByDescending(appointment => appointment.StartsAt).ThenByDescending(appointment => appointment.Id);

  /// <summary>Inclusive Bogotá dates of the appointment's start.</summary>
  private static IQueryable<Appointment> FilterByStartDate(IQueryable<Appointment> appointments, DateOnly? from, DateOnly? to)
  {
    if (from is { } fromDate)
    {
      var startUtc = PagingExtensions.StartOfBogotaDayUtc(fromDate);
      appointments = appointments.Where(appointment => appointment.StartsAt >= startUtc);
    }

    if (to is { } toDate)
    {
      var endUtc = PagingExtensions.EndOfBogotaDayUtc(toDate);
      appointments = appointments.Where(appointment => appointment.StartsAt < endUtc);
    }

    return appointments;
  }

  private async Task<IReadOnlyList<AppointmentServiceOption>> LoadServiceOptionsAsync(Guid staffProfileId, CancellationToken cancellationToken)
  {
    var offerings = await _dbContext.GetBookableAsync([staffProfileId], cancellationToken);
    return offerings
        .Select(offering => new AppointmentServiceOption(
            offering.GeneralServiceId, offering.Name, offering.CategoryId, offering.CategoryName, offering.DurationMinutes))
        .ToArray();
  }

  private async Task<AppointmentView> UpdateAppointmentCoreAsync(
      Guid appointmentId,
      Guid staffProfileId,
      AppointmentUpdateRequest request,
      bool canChangeDuration,
      CancellationToken cancellationToken)
  {
    ValidateUpdateRequest(request);

    Appointment appointment;
    StaffProfile staffProfile;
    DateTime previousStartsAt;
    DateTime previousEndsAt;
    Guid? previousGeneralServiceId;
    await using (var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken))
    {
      await _dbContext.LockStaffProfileAsync(staffProfileId, cancellationToken);
      staffProfile = await LoadActiveStaffProfileAsync(staffProfileId, cancellationToken);
      appointment = await _dbContext.Appointments
          .SingleOrDefaultAsync(
              candidate => candidate.Id == appointmentId && candidate.StaffProfileId == staffProfileId,
              cancellationToken)
          ?? throw new KeyNotFoundException("No encontramos la cita.");

      if (appointment.Status is not (AppointmentStatus.Pending or AppointmentStatus.Confirmed))
      {
        throw new ValidationProblemException(new Dictionary<string, string[]>
        {
          ["status"] = ["Esta cita ya no se puede editar: solo se editan citas pendientes o confirmadas."]
        });
      }

      // A different service must be offered and brings its duration; otherwise the reserved duration is kept
      // (for an appointment from before services, its current length).
      AppointmentServiceReservation? newService = null;
      var baseDuration = appointment.DurationMinutes;
      if (request.GeneralServiceId is { } generalServiceId && generalServiceId != appointment.GeneralServiceId)
      {
        newService = await ResolveOfferingAsync(staffProfileId, generalServiceId, cancellationToken);
        baseDuration = newService.DurationMinutes;
      }

      var duration = ResolveDuration(baseDuration, request.DurationMinutes, canChangeDuration);
      await EnsureSlotIsAvailableAsync(
          staffProfileId, request.StartsAtUtc, request.StartsAtUtc.AddMinutes(duration), appointment.Id, requireGrid: false, cancellationToken);

      previousStartsAt = appointment.StartsAt;
      previousEndsAt = appointment.EndsAt;
      previousGeneralServiceId = appointment.GeneralServiceId;
      appointment.UpdateBooking(
          request.CustomerName,
          request.CustomerEmail,
          request.CustomerPhone,
          request.StartsAtUtc,
          duration,
          newService,
          request.Notes,
          _timeProvider.GetUtcNow().UtcDateTime);

      await _dbContext.SaveChangesAsync(cancellationToken);
      await transaction.CommitAsync(cancellationToken);
    }

    var notificationContext = new AppointmentNotificationContext(
        staffProfile.UserId, staffProfile.DisplayName, appointment.CustomerUserId, appointment.CustomerName, appointment.StartsAt, appointment.Id);

    // BUG-115: "Nueva fecha" only when the date or time moved; a new service or duration gets its own message, and
    // notes or contact data alone notify nobody.
    if (appointment.StartsAt != previousStartsAt)
    {
      await _notificationService.NotifyCustomerOfAppointmentUpdateAsync(notificationContext, cancellationToken);
    }
    else if (appointment.EndsAt != previousEndsAt || appointment.GeneralServiceId != previousGeneralServiceId)
    {
      await _notificationService.NotifyCustomerOfAppointmentDetailsChangeAsync(notificationContext, cancellationToken);
    }

    if (appointment.StartsAt != previousStartsAt)
    {
      await _notificationService.NotifyStaffOfAppointmentRescheduledAsync(notificationContext, cancellationToken);
    }

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
        ["status"] = ["El estado debe ser Confirmada, Completada, Cancelada o No asistió."]
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
        ["status"] = ["Esta cita ya fue cerrada (completada, cancelada o no asistida) y su estado no se puede cambiar."]
      });
    }

    appointment.UpdateStatus(request.Status, _timeProvider.GetUtcNow().UtcDateTime);
    await _dbContext.SaveChangesAsync(cancellationToken);

    if (request.Status == AppointmentStatus.Cancelled)
    {
      var notificationContext = new AppointmentNotificationContext(
          staffProfile.UserId, staffProfile.DisplayName, appointment.CustomerUserId, appointment.CustomerName, appointment.StartsAt, appointment.Id);

      await _notificationService.NotifyCustomerOfAppointmentCancellationAsync(notificationContext, cancellationToken);
      await _notificationService.NotifyStaffOfAppointmentCancelledAsync(notificationContext, cancellationToken);
    }

    if (request.Status == AppointmentStatus.Confirmed)
    {
      await _notificationService.NotifyCustomerOfAppointmentConfirmationAsync(
          new AppointmentNotificationContext(staffProfile.UserId, staffProfile.DisplayName, appointment.CustomerUserId, appointment.CustomerName, appointment.StartsAt, appointment.Id),
          cancellationToken);
    }

    return Map(appointment);
  }

  /// <summary>The professional's bookable offering of that general service, as the reservation to store (409 if none).</summary>
  private async Task<AppointmentServiceReservation> ResolveOfferingAsync(
      Guid staffProfileId, Guid generalServiceId, CancellationToken cancellationToken)
  {
    var offering = await _dbContext.FindBookableAsync(staffProfileId, generalServiceId, cancellationToken)
        ?? throw new ConflictException(ServiceNotOfferedMessage);
    return new AppointmentServiceReservation(offering.GeneralServiceId, offering.Name, offering.DurationMinutes);
  }

  /// <summary>F3: only administration may set a duration different from the default one.</summary>
  private static int ResolveDuration(int defaultDuration, int? requestedDuration, bool canChangeDuration)
  {
    if (requestedDuration is not { } requested || requested == defaultDuration)
    {
      return defaultDuration;
    }

    if (!canChangeDuration)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
      {
        ["durationMinutes"] = [StaffCannotChangeDurationMessage]
      });
    }

    return requested;
  }

  private async Task<User> LoadActiveUserAsync(Guid userId, CancellationToken cancellationToken)
  {
    return await _dbContext.Users
        .SingleOrDefaultAsync(user => user.Id == userId && user.IsActive, cancellationToken)
        ?? throw new KeyNotFoundException("No encontramos tu usuario. Inicia sesión de nuevo.");
  }

  private async Task<StaffProfile> LoadActiveStaffProfileAsync(Guid staffProfileId, CancellationToken cancellationToken)
  {
    var staffProfile = await _dbContext.StaffProfiles
        .Include(profile => profile.User)
        .SingleOrDefaultAsync(profile => profile.Id == staffProfileId, cancellationToken)
        ?? throw new KeyNotFoundException("No encontramos este profesional.");

    if (!staffProfile.IsActive || !staffProfile.User.IsActive)
    {
      throw new KeyNotFoundException("No encontramos este profesional.");
    }

    return staffProfile;
  }

  private async Task<StaffProfile> LoadActiveStaffProfileByUserIdAsync(Guid userId, CancellationToken cancellationToken)
  {
    var staffProfile = await _dbContext.StaffProfiles
        .Include(profile => profile.User)
        .SingleOrDefaultAsync(profile => profile.UserId == userId, cancellationToken)
        ?? throw new KeyNotFoundException("Tu usuario no tiene un perfil de profesional activo.");

    if (!staffProfile.IsActive || !staffProfile.User.IsActive)
    {
      throw new KeyNotFoundException("Tu usuario no tiene un perfil de profesional activo.");
    }

    return staffProfile;
  }

  /// <summary>
  /// The time must be inside one availability rule (for customer bookings, on its 15-minute grid: F1), within one
  /// Bogotá day, and must not overlap an absence or another pending/confirmed appointment. Called under the
  /// professional's lock, so what it reads is what the previous change of the same agenda committed.
  /// </summary>
  private async Task EnsureSlotIsAvailableAsync(
      Guid staffProfileId,
      DateTime startsAtUtc,
      DateTime endsAtUtc,
      Guid? excludedAppointmentId,
      bool requireGrid,
      CancellationToken cancellationToken)
  {
    // Availability rules are stored as Bogota wall-clock TimeOnly values, so the UTC
    // instant must be converted to local time before comparing against them.
    var startsAtLocal = BogotaClock.ToLocal(startsAtUtc);
    var endsAtLocal = BogotaClock.ToLocal(endsAtUtc);

    if (startsAtLocal.Date != endsAtLocal.Date)
    {
      throw new ConflictException(SlotNotAvailableMessage);
    }

    var startTime = TimeOnly.FromDateTime(startsAtLocal);
    var endTime = TimeOnly.FromDateTime(endsAtLocal);

    var coveringRules = await _dbContext.StaffAvailabilityRules
        .Where(rule => rule.StaffProfileId == staffProfileId
            && rule.IsActive
            && rule.DayOfWeek == startsAtLocal.DayOfWeek
            && rule.StartTime <= startTime
            && endTime <= rule.EndTime)
        .ToListAsync(cancellationToken);

    var coveredByRule = requireGrid
        ? coveringRules.Any(rule => AppointmentSlotGrid.IsSlotOf(rule, startTime, endTime))
        : coveringRules.Count > 0;

    if (!coveredByRule)
    {
      throw new ConflictException(SlotNotAvailableMessage);
    }

    var overlapsUnavailablePeriod = await _dbContext.StaffUnavailablePeriods
        .AnyAsync(
            period => period.StaffProfileId == staffProfileId
                && period.StartsAt < endsAtUtc
                && startsAtUtc < period.EndsAt,
            cancellationToken);

    if (overlapsUnavailablePeriod)
    {
      throw new ConflictException(SlotNotAvailableMessage);
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
      throw new ConflictException(SlotNotAvailableMessage);
    }
  }

  private static void ValidateManualCreate(DateTime startsAtUtc, Guid generalServiceId, int? durationMinutes)
  {
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    if (startsAtUtc == default)
    {
      errors["startsAtUtc"] = ["Indica la fecha y la hora de la cita."];
    }
    else if (startsAtUtc.Kind != DateTimeKind.Utc)
    {
      errors["startsAtUtc"] = ["La fecha y la hora de la cita no tienen el formato esperado."];
    }

    if (generalServiceId == Guid.Empty)
    {
      errors["generalServiceId"] = ["Elige el servicio de la cita."];
    }

    AddDurationError(errors, durationMinutes);
    ThrowIfAnyErrors(errors);
  }

  private static void AddDurationError(Dictionary<string, string[]> errors, int? durationMinutes)
  {
    if (durationMinutes is { } minutes
        && minutes is < AppointmentServiceReservation.MinDurationMinutes or > AppointmentServiceReservation.MaxDurationMinutes)
    {
      errors["durationMinutes"] =
          [$"La duración debe estar entre {AppointmentServiceReservation.MinDurationMinutes} y {AppointmentServiceReservation.MaxDurationMinutes} minutos."];
    }
  }

  private static void ValidateCustomerCreateRequest(CustomerAppointmentCreateRequest request)
  {
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    if (request.StaffProfileId == Guid.Empty)
    {
      errors["staffProfileId"] = ["Elige un profesional."];
    }

    if (request.GeneralServiceId == Guid.Empty)
    {
      errors["generalServiceId"] = ["Elige el servicio que quieres reservar."];
    }

    if (request.StartsAtUtc == default)
    {
      errors["startsAtUtc"] = ["Indica la fecha y la hora de la cita."];
    }
    else if (request.StartsAtUtc.Kind != DateTimeKind.Utc)
    {
      errors["startsAtUtc"] = ["La fecha y la hora de la cita no tienen el formato esperado."];
    }

    AddNotesError(errors, request.Notes);

    ThrowIfAnyErrors(errors);
  }

  private static void ValidateUpdateRequest(AppointmentUpdateRequest request)
  {
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    if (request.StartsAtUtc == default)
    {
      errors["startsAtUtc"] = ["Indica la fecha y la hora de la cita."];
    }
    else if (request.StartsAtUtc.Kind != DateTimeKind.Utc)
    {
      errors["startsAtUtc"] = ["La fecha y la hora de la cita no tienen el formato esperado."];
    }

    if (request.GeneralServiceId == Guid.Empty)
    {
      errors["generalServiceId"] = ["Elige el servicio de la cita."];
    }

    AddDurationError(errors, request.DurationMinutes);
    AddCustomerDetailsErrors(errors, request.CustomerName, request.CustomerEmail, request.CustomerPhone, request.Notes);

    ThrowIfAnyErrors(errors);
  }

  private static void ValidateManualSnapshot(string customerName, string? customerEmail, string? customerPhone, string? notes)
  {
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
    // A new appointment's contact email is typed now: the strict rule (BUG-004, BUG-017).
    AddCustomerDetailsErrors(errors, customerName, customerEmail, customerPhone, notes, strictEmail: true);
    ThrowIfAnyErrors(errors);
  }

  /// <summary>
  /// The limits of the Appointment entity, checked before building it (BUG-021): the domain's ArgumentException used to
  /// reach the client as a 400 with the .NET message in English.
  /// </summary>
  private static void AddCustomerDetailsErrors(
      Dictionary<string, string[]> errors, string? customerName, string? customerEmail, string? customerPhone, string? notes,
      bool strictEmail = false)
  {
    var name = customerName?.Trim() ?? string.Empty;
    if (name.Length is < 2 or > 120)
    {
      errors["customerName"] = ["El nombre del cliente debe tener entre 2 y 120 caracteres."];
    }

    if (!string.IsNullOrWhiteSpace(customerEmail)
        && (strictEmail ? !EmailRules.IsValid(customerEmail) : customerEmail.Trim().Length > 256 || !IsValidEmail(customerEmail.Trim())))
    {
      errors["customerEmail"] = [EmailRules.InvalidMessage];
    }

    if (!string.IsNullOrWhiteSpace(customerPhone) && customerPhone.Trim().Length > 40)
    {
      errors["customerPhone"] = ["El teléfono no puede superar 40 caracteres."];
    }

    AddNotesError(errors, notes);
  }

  private static void AddNotesError(Dictionary<string, string[]> errors, string? notes)
  {
    if (!string.IsNullOrWhiteSpace(notes) && notes.Trim().Length > 2000)
    {
      errors["notes"] = ["Las notas no pueden superar 2000 caracteres."];
    }
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
          staffName,
          appointment.GeneralServiceId,
          appointment.GeneralServiceNameSnapshot,
          appointment.ReservedDurationMinutes);

  private static void ThrowIfAnyErrors(Dictionary<string, string[]> errors)
  {
    if (errors.Count > 0)
    {
      throw new ValidationProblemException(errors);
    }
  }
}
