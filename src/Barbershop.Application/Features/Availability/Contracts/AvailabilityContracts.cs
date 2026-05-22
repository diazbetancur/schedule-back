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

public sealed record PublicAvailabilitySlotsResponse(
    Guid StaffProfileId,
    DateOnly From,
    DateOnly To,
    int SlotDurationMinutes,
    IReadOnlyList<PublicAvailabilitySlotResponse> Slots);