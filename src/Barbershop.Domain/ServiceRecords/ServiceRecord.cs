using Barbershop.Domain.Common;
using Barbershop.Domain.Finance;

namespace Barbershop.Domain.ServiceRecords;

/// <summary>
/// A service a professional says they performed, from one of their appointments or without one, waiting for an
/// administrator to confirm it. The values registered by the professional never change; the approval stores the
/// values the administrator confirmed (possibly corrected) next to them, so every correction stays visible.
/// Confirming creates the matching income or membership consumption in the same transaction; a confirmed or
/// rejected record is final.
/// </summary>
public sealed class ServiceRecord
{
  public const int MaxServicesPerRegistration = 10;
  public const int MaxNotesLength = 500;
  public const int MinReasonLength = 3;
  public const int MaxReasonLength = 500;

  private ServiceRecord()
  {
  }

  public ServiceRecord(
      Guid registrationKey,
      int position,
      Guid? appointmentId,
      Guid staffProfileId,
      Guid? serviceId,
      string serviceNameSnapshot,
      DateOnly performedOn,
      Guid? customerId,
      string? customerNameSnapshot,
      string? notes,
      Guid registeredByUserId,
      DateTime registeredAt,
      Guid? generalServiceId = null)
  {
    if (registrationKey == Guid.Empty)
    {
      throw new ArgumentException("A service record needs a registration key.", nameof(registrationKey));
    }

    // New registrations name a general service (ADR-0016 F4); records from before named a rate. One is required.
    if (serviceId is null && generalServiceId is null)
    {
      throw new ArgumentException("A service record needs the general service performed.", nameof(generalServiceId));
    }

    RegistrationKey = registrationKey;
    Position = DomainValidation.EnsureRange(position, 0, MaxServicesPerRegistration - 1, nameof(position));
    AppointmentId = appointmentId;
    StaffProfileId = staffProfileId;
    ServiceId = serviceId;
    GeneralServiceId = generalServiceId;
    ServiceNameSnapshot = DomainValidation.Required(serviceNameSnapshot, nameof(serviceNameSnapshot), 120, 1);
    PerformedOn = performedOn;
    CustomerId = customerId;
    CustomerNameSnapshot = DomainValidation.Optional(customerNameSnapshot, 120);
    Notes = DomainValidation.Optional(notes, MaxNotesLength);
    RegisteredByUserId = registeredByUserId;
    RegisteredAt = DomainValidation.EnsureUtc(registeredAt, nameof(registeredAt));
    Status = ServiceRecordStatus.Pending;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();

  /// <summary>Sent by the client once per registration (one or more services), so a retried request is not stored twice.</summary>
  public Guid RegistrationKey { get; private set; }

  /// <summary>Order of the service within its registration.</summary>
  public int Position { get; private set; }

  public Guid? AppointmentId { get; private set; }

  // As registered by the professional; never changed afterwards.
  public Guid StaffProfileId { get; private set; }

  /// <summary>The rate registered; only records from before general services have it (ADR-0016 B4).</summary>
  public Guid? ServiceId { get; private set; }

  /// <summary>The general service registered (ADR-0016 F4); null on records from before, which named a rate.</summary>
  public Guid? GeneralServiceId { get; private set; }

  /// <summary>Name registered: the general service's for new records, the rate's for older ones.</summary>
  public string ServiceNameSnapshot { get; private set; } = string.Empty;
  public DateOnly PerformedOn { get; private set; }
  public Guid? CustomerId { get; private set; }

  /// <summary>Name of the customer as known at registration (from the appointment or typed by the professional).</summary>
  public string? CustomerNameSnapshot { get; private set; }

  public string? Notes { get; private set; }
  public Guid RegisteredByUserId { get; private set; }
  public DateTime RegisteredAt { get; private set; }

  public ServiceRecordStatus Status { get; private set; }

  // Confirmed by the administrator.
  public ServiceRecordOperationType? OperationType { get; private set; }
  public Guid? ApprovedServiceId { get; private set; }

  /// <summary>
  /// The general service confirmed; the confirmed rate belongs to it (ADR-0016 D12). Null on records confirmed before
  /// general services existed.
  /// </summary>
  public Guid? ApprovedGeneralServiceId { get; private set; }
  public string? ApprovedServiceNameSnapshot { get; private set; }
  public Guid? ApprovedStaffProfileId { get; private set; }
  public Guid? ApprovedCustomerId { get; private set; }
  public DateOnly? ApprovedPerformedOn { get; private set; }

  /// <summary>Money actually charged; only for <see cref="ServiceRecordOperationType.Paid"/>.</summary>
  public int? ChargedAmount { get; private set; }

  /// <summary>The single income created by the confirmation of a paid service.</summary>
  public Guid? IncomeEntryId { get; private set; }

  public Guid? CustomerMembershipId { get; private set; }

  /// <summary>The single membership consumption created by the confirmation of a membership consumption.</summary>
  public Guid? MembershipConsumptionId { get; private set; }

  /// <summary>For a loyalty reward: the approved redemption this confirmation delivered (one record per redemption).</summary>
  public Guid? LoyaltyRedemptionId { get; private set; }

  // For a loyalty reward only (ADR-0015 R14): the professional's commission the business covers, computed with the
  // service's base price and split at confirmation and the same rule as an income. Informative: no income, expense
  // or payment is created.
  public int? RewardBasePriceSnapshot { get; private set; }
  public int? RewardBusinessPercentageSnapshot { get; private set; }
  public int? RewardProfessionalCommission { get; private set; }

  /// <summary>The administrator's decision; only a paid service with an identified customer and points can accrue.</summary>
  public bool AccruesLoyaltyPoints { get; private set; }

  /// <summary>Points of the service when it was confirmed as accruing; no points are credited yet.</summary>
  public int? LoyaltyPointsSnapshot { get; private set; }

  public string? ReviewNotes { get; private set; }
  public Guid? ApprovedByUserId { get; private set; }
  public DateTime? ApprovedAt { get; private set; }

  public string? RejectionReason { get; private set; }
  public Guid? RejectedByUserId { get; private set; }
  public DateTime? RejectedAt { get; private set; }

  /// <summary>Mapped to PostgreSQL's xmin system column; used as an optimistic concurrency token.</summary>
  public uint Version { get; private set; }

  public void Approve(ServiceRecordApproval approval, Guid approvedByUserId, DateTime approvedAt)
  {
    EnsurePending();
    approval.EnsureConsistent();

    OperationType = approval.OperationType;
    ApprovedServiceId = approval.ServiceId;
    ApprovedGeneralServiceId = approval.GeneralServiceId;
    ApprovedServiceNameSnapshot = DomainValidation.Required(approval.ServiceNameSnapshot, nameof(approval.ServiceNameSnapshot), 120, 1);
    ApprovedStaffProfileId = approval.StaffProfileId;
    ApprovedCustomerId = approval.CustomerId;
    ApprovedPerformedOn = approval.PerformedOn;
    ChargedAmount = approval.ChargedAmount;
    IncomeEntryId = approval.IncomeEntryId;
    CustomerMembershipId = approval.CustomerMembershipId;
    MembershipConsumptionId = approval.MembershipConsumptionId;
    LoyaltyRedemptionId = approval.LoyaltyRedemptionId;
    if (approval.OperationType == ServiceRecordOperationType.LoyaltyReward)
    {
      RewardBasePriceSnapshot = approval.RewardBasePriceSnapshot;
      RewardBusinessPercentageSnapshot = approval.RewardBusinessPercentageSnapshot;
      RewardProfessionalCommission = approval.RewardBasePriceSnapshot!.Value
          - IncomeEntry.BusinessShare(approval.RewardBasePriceSnapshot.Value, approval.RewardBusinessPercentageSnapshot!.Value);
    }

    AccruesLoyaltyPoints = approval.AccruesLoyaltyPoints;
    LoyaltyPointsSnapshot = approval.AccruesLoyaltyPoints ? approval.LoyaltyPointsSnapshot : null;
    ReviewNotes = DomainValidation.Optional(approval.ReviewNotes, MaxNotesLength);
    ApprovedByUserId = approvedByUserId;
    ApprovedAt = DomainValidation.EnsureUtc(approvedAt, nameof(approvedAt));
    Status = ServiceRecordStatus.Approved;
  }

  public void Reject(string reason, Guid rejectedByUserId, DateTime rejectedAt)
  {
    EnsurePending();
    RejectionReason = DomainValidation.Required(reason, nameof(reason), MaxReasonLength, MinReasonLength);
    RejectedByUserId = rejectedByUserId;
    RejectedAt = DomainValidation.EnsureUtc(rejectedAt, nameof(rejectedAt));
    Status = ServiceRecordStatus.Rejected;
  }

  private void EnsurePending()
  {
    if (Status != ServiceRecordStatus.Pending)
    {
      throw new InvalidOperationException($"The service record is {Status}; only pending records can be reviewed.");
    }
  }
}

/// <summary>What the administrator confirmed, plus the income or consumption the confirmation created.</summary>
public sealed record ServiceRecordApproval(
    ServiceRecordOperationType OperationType,
    Guid ServiceId,
    string ServiceNameSnapshot,
    Guid StaffProfileId,
    Guid? CustomerId,
    DateOnly PerformedOn,
    int? ChargedAmount,
    Guid? IncomeEntryId,
    Guid? CustomerMembershipId,
    Guid? MembershipConsumptionId,
    bool AccruesLoyaltyPoints,
    int? LoyaltyPointsSnapshot,
    string? ReviewNotes,
    Guid? LoyaltyRedemptionId = null,
    int? RewardBasePriceSnapshot = null,
    int? RewardBusinessPercentageSnapshot = null,
    Guid? GeneralServiceId = null)
{
  internal void EnsureConsistent()
  {
    var valid = OperationType switch
    {
      ServiceRecordOperationType.Paid =>
          ChargedAmount is > 0 && IncomeEntryId is not null && CustomerMembershipId is null && MembershipConsumptionId is null
          && NoReward,
      ServiceRecordOperationType.MembershipConsumption =>
          ChargedAmount is null && IncomeEntryId is null && CustomerId is not null
          && CustomerMembershipId is not null && MembershipConsumptionId is not null && NoReward,
      ServiceRecordOperationType.Courtesy =>
          ChargedAmount is null && IncomeEntryId is null && CustomerMembershipId is null && MembershipConsumptionId is null
          && NoReward,
      // No money and no points; the business covers the professional's commission (ADR-0009, ADR-0015).
      ServiceRecordOperationType.LoyaltyReward =>
          ChargedAmount is null && IncomeEntryId is null && CustomerMembershipId is null && MembershipConsumptionId is null
          && CustomerId is not null && LoyaltyRedemptionId is not null
          && RewardBasePriceSnapshot is >= 0 && RewardBusinessPercentageSnapshot is >= 0 and <= 100,
      _ => false
    };

    if (!valid)
    {
      throw new InvalidOperationException($"Incomplete or inconsistent approval for a {OperationType} service record.");
    }

    // Only a paid service of an identified customer, with points configured, may accrue.
    if (AccruesLoyaltyPoints
        && (OperationType != ServiceRecordOperationType.Paid || CustomerId is null || LoyaltyPointsSnapshot is not > 0))
    {
      throw new InvalidOperationException("Only a paid service of an identified customer with configured points can accrue loyalty points.");
    }
  }

  private bool NoReward => LoyaltyRedemptionId is null && RewardBasePriceSnapshot is null && RewardBusinessPercentageSnapshot is null;
}

public enum ServiceRecordStatus
{
  Pending = 1,
  Approved = 2,
  Rejected = 3
}

public enum ServiceRecordOperationType
{
  /// <summary>Charged to the customer: one service income, split with the professional as usual.</summary>
  Paid = 1,

  /// <summary>Taken from a sold membership through the membership ledger: no income and no commission.</summary>
  MembershipConsumption = 2,

  /// <summary>Given for free: no income, no commission, no points.</summary>
  Courtesy = 3,

  /// <summary>Delivers an approved service reward: no income, no points; the business covers the commission.</summary>
  LoyaltyReward = 4
}
