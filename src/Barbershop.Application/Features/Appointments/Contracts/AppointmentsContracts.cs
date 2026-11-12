using Barbershop.Domain.Appointments;

namespace Barbershop.Application.Appointments;

/// <summary>
/// The customer books one general service the professional offers (ADR-0016 D7, D8). The slot lasts what that service
/// takes the professional; the start must be one of the slots availability offers (15-minute grid, F1).
/// </summary>
public sealed record CustomerAppointmentCreateRequest(
    Guid StaffProfileId,
    Guid GeneralServiceId,
    DateTime StartsAtUtc,
    string? Notes);

/// <summary>
/// The customer moves their own appointment to another slot with the same barber. The service and the reserved
/// duration never change (D11).
/// </summary>
public sealed record CustomerAppointmentRescheduleRequest(DateTime StartsAtUtc);

/// <summary>
/// A professional adds an appointment to their own agenda (F3): the service must be one they offer and the duration is
/// the offering's. <c>DurationMinutes</c> may be omitted; a different value is rejected (only administration changes it).
/// </summary>
public sealed record StaffManualAppointmentCreateRequest(
    DateTime StartsAtUtc,
    Guid GeneralServiceId,
    string CustomerName,
    string? CustomerEmail,
    string? CustomerPhone,
    string? Notes,
    int? DurationMinutes = null);

/// <summary>
/// Administration adds an appointment (F3): the service must be one the professional offers. <c>DurationMinutes</c>
/// defaults to the offering's and may be changed for this appointment only (10 to 240 minutes).
/// </summary>
public sealed record AdminManualAppointmentCreateRequest(
    Guid StaffProfileId,
    DateTime StartsAtUtc,
    Guid GeneralServiceId,
    string CustomerName,
    string? CustomerEmail,
    string? CustomerPhone,
    string? Notes,
    int? DurationMinutes = null);

/// <summary>
/// Edits an appointment. <c>GeneralServiceId</c> null keeps the current service (an appointment from before the
/// service existed keeps having none); a different one must be offered by the professional and brings its duration.
/// <c>DurationMinutes</c> null keeps the reserved duration (or the offering's, when the service changes); only
/// administration may send a different one.
/// </summary>
public sealed record AppointmentUpdateRequest(
    DateTime StartsAtUtc,
    string CustomerName,
    string? CustomerEmail,
    string? CustomerPhone,
    string? Notes,
    Guid? GeneralServiceId = null,
    int? DurationMinutes = null);

/// <summary>A service that can be booked with a professional right now, and how long it takes them. No price.</summary>
public sealed record AppointmentServiceOption(
    Guid GeneralServiceId,
    string Name,
    Guid CategoryId,
    string CategoryName,
    int DurationMinutes);

public sealed record AppointmentStatusUpdateRequest(AppointmentStatus Status);

public sealed record AppointmentView(
    Guid Id,
    Guid StaffProfileId,
    Guid? CustomerUserId,
    string CustomerName,
    string? CustomerEmail,
    string? CustomerPhone,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    AppointmentStatus Status,
    AppointmentSource Source,
    string? Notes,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc,
    string? StaffName = null,
    Guid? GeneralServiceId = null,
    string? GeneralServiceName = null,
    int? ReservedDurationMinutes = null);

/// <summary>
/// Filters of <c>GET /admin/appointments</c> (ADR-0016 B7). <c>From</c>/<c>To</c> are inclusive Bogotá dates of the
/// start. <c>Status</c> is an <see cref="AppointmentStatus"/> name or a comma-separated list of them (any of). <c>Customer</c> matches part of the customer's name,
/// email or phone. <c>Sort</c> is "Asc" (soonest first) or "Desc" (newest start first); without it the agenda scope is
/// "Asc" and the history "Desc". Ties are broken by id in the same direction. <c>Scope</c> (<see cref="AppointmentListScopes"/>) separates
/// the agenda (from today on, any length) from the history (at most 30 days per query, the last 30 by default).
/// </summary>
public sealed record AdminAppointmentListQuery(
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? StaffProfileId = null,
    string? Status = null,
    Guid? GeneralServiceId = null,
    string? Customer = null,
    int? Page = null,
    int? PageSize = null,
    string? Sort = null,
    string? Scope = null);

/// <summary>
/// Filters of <c>GET /staff/appointments</c>: the professional's own agenda. <c>Sort</c> as in
/// <see cref="AdminAppointmentListQuery"/> (soonest first in the agenda, newest first in the history by default).
/// </summary>
public sealed record StaffAppointmentListQuery(
    DateOnly? From = null,
    DateOnly? To = null,
    string? Status = null,
    int? Page = null,
    int? PageSize = null,
    string? Sort = null,
    string? Scope = null);

/// <summary>
/// "Agenda": appointments starting today or later; <c>From</c> is raised to today and the range has no length limit,
/// because it is the queue still to attend. "History" (the default): any period, but at most 30 calendar days per
/// query and the last 30 days when no range is given (<c>HistoryRange</c>).
/// </summary>
public static class AppointmentListScopes
{
    public const string Agenda = "Agenda";
    public const string History = "History";
}

public static class AppointmentListSort
{
    public const string Asc = "Asc";
    public const string Desc = "Desc";
}

/// <summary>
/// Filters of <c>GET /customer/appointments</c>. <c>Scope</c>: "Upcoming" = still active (pending or confirmed) and
/// starting in the future, soonest first; "Past" = everything else (already started, cancelled or no-show), newest
/// first; omitted = all, newest first. <c>From</c>/<c>To</c> (inclusive Bogotá dates of the start) narrow it further.
/// "Upcoming" has no default range; any other scope is a history: at most 30 days per query, the last 30 by default.
/// </summary>
public sealed record CustomerAppointmentListQuery(
    string? Scope = null,
    string? Status = null,
    DateOnly? From = null,
    DateOnly? To = null,
    int? Page = null,
    int? PageSize = null);

public static class CustomerAppointmentScopes
{
    public const string Upcoming = "Upcoming";
    public const string Past = "Past";
}
