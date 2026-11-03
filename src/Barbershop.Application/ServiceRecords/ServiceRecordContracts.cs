namespace Barbershop.Application.ServiceRecords;

// Status travels as "Pending" | "Approved" | "Rejected"; OperationType as "Paid" | "MembershipConsumption" |
// "Courtesy" | "LoyaltyReward", so invalid values come back as field validation errors.

public sealed record ServiceRecordCatalogItem(Guid Id, string Name);

/// <summary>
/// One registration of one or more services. With <c>AppointmentId</c> the date and the customer come from the
/// appointment (which must be the professional's own); without it, <c>PerformedOn</c> is required and
/// <c>CustomerName</c> is optional. <c>IdempotencyKey</c> is generated once per intended registration.
/// </summary>
public sealed record ServiceRecordRegisterRequest(
    Guid? AppointmentId,
    DateOnly? PerformedOn,
    string? CustomerName,
    IReadOnlyList<Guid>? ServiceIds,
    string? Notes,
    Guid IdempotencyKey);

public sealed record ServiceRecordApprovalView(
    string OperationType,
    Guid ServiceId,
    string ServiceName,
    Guid StaffProfileId,
    string? StaffName,
    Guid? CustomerId,
    string? CustomerName,
    DateOnly PerformedOn,
    int? ChargedAmount,
    Guid? IncomeEntryId,
    Guid? CustomerMembershipId,
    Guid? MembershipConsumptionId,
    bool AccruesLoyaltyPoints,
    int? LoyaltyPointsSnapshot,
    string? ReviewNotes,
    DateTime ApprovedAt,
    string? ApprovedByName);

public sealed record ServiceRecordView(
    Guid Id,
    string Status,
    Guid? AppointmentId,
    DateTime? AppointmentStartsAt,
    Guid StaffProfileId,
    string? StaffName,
    Guid ServiceId,
    string ServiceName,
    DateOnly PerformedOn,
    Guid? CustomerId,
    string? CustomerName,
    string? Notes,
    DateTime RegisteredAt,
    string? RegisteredByName,
    ServiceRecordApprovalView? Approval,
    string? RejectionReason,
    DateTime? RejectedAt,
    string? RejectedByName,
    uint Version);

/// <summary>
/// Confirms a pending record. Service, professional, customer and date may be corrected; the registered values are
/// kept. <c>ChargedAmount</c> is required (greater than zero) for "Paid" and must be omitted otherwise;
/// <c>CustomerMembershipId</c> is required for "MembershipConsumption". <c>Version</c> is the one read.
/// </summary>
public sealed record ServiceRecordApproveRequest(
    uint? Version,
    string? OperationType,
    Guid ServiceId,
    Guid StaffProfileId,
    Guid? CustomerId,
    DateOnly PerformedOn,
    int? ChargedAmount,
    Guid? CustomerMembershipId,
    bool AccruesLoyaltyPoints,
    string? Notes);

public sealed record ServiceRecordRejectRequest(uint? Version, string? Reason);

public sealed record ServiceRecordStaffOption(Guid Id, string DisplayName, bool IsActive);

public sealed record ServiceRecordServiceOption(Guid Id, string Name, int BasePrice, int LoyaltyPoints, bool IsActive);

public sealed record ServiceRecordOptionsView(
    IReadOnlyList<ServiceRecordServiceOption> Services,
    IReadOnlyList<ServiceRecordStaffOption> Staff,
    bool LoyaltyProgramEnabled);

public sealed record ServiceRecordMembershipOption(Guid Id, string PlanName, string Status, DateOnly StartsOn, DateOnly EndsOn);

/// <summary>Only what identifies and selects a profile: see <see cref="ServiceRecordCustomerPrivacy"/>.</summary>
public sealed record ServiceRecordCustomerOption(
    Guid Id,
    string FullName,
    string PhoneHint,
    IReadOnlyList<ServiceRecordMembershipOption> Memberships);

/// <summary>
/// A possible duplicate of a customer being created, reduced for approvers. <c>CustomerId</c> is only set for an
/// existing customer profile (<c>Kind</c> "customer"); for a matching account (<c>Kind</c> "account") nothing
/// identifies the user.
/// </summary>
public sealed record ServiceRecordCustomerDuplicate(
    string Kind,
    Guid? CustomerId,
    string FullName,
    string? EmailHint,
    string? PhoneHint,
    IReadOnlyList<string> MatchedOn);

/// <summary>Whether the current user can register services: it needs an active professional profile of their own.</summary>
public sealed record ServiceRecordEligibilityView(bool HasStaffProfile, Guid? StaffProfileId, string? DisplayName);

/// <summary>
/// One of the professional's own recent appointments that can hold registered services (already started, not cancelled
/// nor missed; completed ones included). <c>RegisteredServiceIds</c> are the services already registered and not rejected.
/// </summary>
public sealed record ServiceRecordAppointmentOption(
    Guid Id,
    DateTime StartsAt,
    string CustomerName,
    string Status,
    IReadOnlyList<Guid> RegisteredServiceIds);

/// <summary>
/// Operations that may duplicate a pending record, found through real relations only (same professional, service and
/// date; same appointment). They are warnings: matching customer, date and service can be legitimate, so nothing is blocked.
/// </summary>
public sealed record ServiceRecordRelatedView(
    IReadOnlyList<ServiceRecordRelatedIncome> ManualIncomes,
    IReadOnlyList<ServiceRecordRelatedRecord> OtherRecords,
    IReadOnlyList<ServiceRecordRelatedConsumption> MembershipConsumptions,
    string? AppointmentStatus);

public sealed record ServiceRecordRelatedIncome(Guid Id, int Amount, DateOnly OccurredOn, DateTime CreatedAt);

public sealed record ServiceRecordRelatedRecord(Guid Id, string Status, string ServiceName, DateOnly PerformedOn, DateTime RegisteredAt);

public sealed record ServiceRecordRelatedConsumption(Guid Id, Guid CustomerMembershipId, string ServiceName, DateOnly ServiceDate, bool IsFromServiceRecord);
