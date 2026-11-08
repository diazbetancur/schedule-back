namespace Barbershop.Application.Staff.Admin;

// ADR-0016 B2: the general services a professional offers and how long each one takes them (D6). The offering is
// what booking uses: the customer picks one of these services and the slot lasts its duration (D10).

/// <summary>
/// One offering of a professional. <c>GeneralServiceIsActive</c> and <c>CategoryIsActive</c> tell whether the
/// service can be booked right now (an inactive general service keeps its offering, but it is not bookable).
/// </summary>
public sealed record StaffServiceOfferingView(
    Guid GeneralServiceId,
    string GeneralServiceName,
    Guid CategoryId,
    string CategoryName,
    int DurationMinutes,
    bool IsActive,
    bool GeneralServiceIsActive,
    bool CategoryIsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

/// <summary>The offerings of one professional; the default duration is only a suggestion for new offerings.</summary>
public sealed record StaffServiceOfferingsView(
    Guid StaffProfileId,
    int DefaultAppointmentDurationMinutes,
    IReadOnlyList<StaffServiceOfferingView> Offerings);

public sealed record StaffServiceOfferingRequest(Guid GeneralServiceId, int DurationMinutes, bool IsActive = true);

/// <summary>
/// Full replacement: afterwards the professional has exactly these offerings (any other one is removed). Adding an
/// offering, or activating one, needs an active general service.
/// </summary>
public sealed record StaffServiceOfferingsReplaceRequest(IReadOnlyList<StaffServiceOfferingRequest>? Offerings);

public interface IAdminStaffOfferingsService
{
    Task<StaffServiceOfferingsView> GetAsync(Guid staffProfileId, CancellationToken cancellationToken = default);

    Task<StaffServiceOfferingsView> ReplaceAsync(
        Guid staffProfileId, StaffServiceOfferingsReplaceRequest request, CancellationToken cancellationToken = default);
}
