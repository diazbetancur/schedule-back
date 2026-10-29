using Barbershop.Domain.Appointments;

namespace Barbershop.Application.Appointments;

public sealed record CustomerAppointmentCreateRequest(
    Guid StaffProfileId,
    DateTime StartsAtUtc,
    string? Notes);

/// <summary>The customer moves their own appointment to another slot with the same barber.</summary>
public sealed record CustomerAppointmentRescheduleRequest(DateTime StartsAtUtc);

public sealed record StaffManualAppointmentCreateRequest(
    DateTime StartsAtUtc,
    DateTime? EndsAtUtc,
    string CustomerName,
    string? CustomerEmail,
    string? CustomerPhone,
    string? Notes);

public sealed record AdminManualAppointmentCreateRequest(
    Guid StaffProfileId,
    DateTime StartsAtUtc,
    DateTime? EndsAtUtc,
    string CustomerName,
    string? CustomerEmail,
    string? CustomerPhone,
    string? Notes);

public sealed record AppointmentUpdateRequest(
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    string CustomerName,
    string? CustomerEmail,
    string? CustomerPhone,
    string? Notes);

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
    string? StaffName = null);
