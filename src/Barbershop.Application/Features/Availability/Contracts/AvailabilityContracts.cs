namespace Barbershop.Application.Availability;

public sealed record AvailabilityRuleRequest(
    int DayOfWeek,
    TimeOnly StartTime,
    TimeOnly EndTime,
    bool IsActive = true);

public sealed record AvailabilityRuleResponse(
    Guid Id,
    int DayOfWeek,
    TimeOnly StartTime,
    TimeOnly EndTime,
    bool IsActive);

public sealed record UnavailablePeriodCreateRequest(
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    string? Reason);

public sealed record UnavailablePeriodUpdateRequest(
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    string? Reason);

public sealed record UnavailablePeriodResponse(
    Guid Id,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    string? Reason,
    DateTime CreatedAtUtc);

public sealed record AvailabilitySummaryResponse(
    Guid StaffProfileId,
    int DefaultAppointmentDurationMinutes,
    IReadOnlyList<AvailabilityRuleResponse> Rules,
    IReadOnlyList<UnavailablePeriodResponse> UnavailablePeriods);

public sealed record PublicAvailabilitySlotResponse(
    DateTime StartAtUtc,
    DateTime EndAtUtc);

/// <summary>
/// Slots of one service (or of a fixed duration, to reschedule an existing appointment). Each slot lasts
/// <c>SlotDurationMinutes</c>. ADR-0022: starts are one duration apart from the start of each availability rule, and
/// after an occupied time the grid resumes where it ends, so <c>SlotStepMinutes</c> (now equal to the duration) is the
/// nominal step, not a guaranteed distance between consecutive slots.
/// </summary>
public sealed record PublicAvailabilitySlotsResponse(
    Guid StaffProfileId,
    DateOnly From,
    DateOnly To,
    int SlotDurationMinutes,
    IReadOnlyList<PublicAvailabilitySlotResponse> Slots,
    Guid? GeneralServiceId = null,
    int SlotStepMinutes = 15);