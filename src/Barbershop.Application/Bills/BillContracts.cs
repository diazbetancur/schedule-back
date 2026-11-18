using Barbershop.Application.Common.Paging;

namespace Barbershop.Application.Bills;

// ADR-0020: bills (internal receipts). Amounts are whole Colombian pesos. Enum values travel as their names:
// Status Open | Closed | Approved | Unapproved | Voided; line Type Service | Product | MembershipConsumption |
// Redemption | Discount | Tip; TipPaymentMode WithBill | Direct.

/// <summary>What the current user may do with this bill right now (the API checks it again on every request).</summary>
public sealed record BillActionsView(
    bool CanEdit,
    bool CanDiscount,
    bool CanClose,
    bool CanCharge,
    bool CanVoid,
    bool CanApprove,
    bool CanDetachAppointment,
    bool CanChangePaymentMethod,
    bool CanUnapprove = false);

public sealed record BillLineView(
    Guid Id,
    int Position,
    string Type,
    Guid? StaffProfileId,
    string? StaffName,
    bool StaffIsActive,
    Guid? GeneralServiceId,
    Guid? ServiceId,
    string Name,
    int Quantity,
    int UnitPrice,
    int Amount,
    int? BusinessPercentage,
    bool IsCourtesy,
    string? Reason,
    Guid? ProductId,
    Guid? CustomerMembershipId,
    string? MembershipName,
    Guid? LoyaltyRedemptionId,
    Guid? AppliesToLineId,
    string? TipPaymentMode,
    /// <summary>The rate's or product's price in the catalog now, to show a change since the line was added.</summary>
    int? CatalogPriceNow,
    /// <summary>Products: units on hand minus the units of other closed bills not approved yet.</summary>
    int? AvailableStock);

public sealed record BillView(
    Guid Id,
    string? Number,
    string Status,
    Guid? AppointmentId,
    DateTime? AppointmentStartsAt,
    string? AppointmentStatus,
    Guid? CustomerId,
    string CustomerName,
    bool IsWalkIn,
    Guid? StaffProfileId,
    string? StaffName,
    DateOnly ServiceDate,
    string? Notes,
    DateTime OpenedAt,
    string? OpenedByName,
    DateTime? ClosedAt,
    string? ClosedByName,
    Guid? PaymentMethodId,
    string? PaymentMethodName,
    int Subtotal,
    int DiscountTotal,
    int TipTotal,
    int DirectTipTotal,
    int Total,
    DateTime? ApprovedAt,
    string? ApprovedByName,
    bool IsSelfApproved,
    DateTime? VoidedAt,
    string? VoidedByName,
    string? VoidReason,
    bool IsOverdue,
    uint Version,
    IReadOnlyList<BillLineView> Lines,
    IReadOnlyList<string> Warnings,
    BillActionsView Actions,
    /// <summary>ADR-0020 stage 4: how many times it was approved, and the last unapproval (when, by whom, why).</summary>
    int ApprovalCount = 0,
    DateTime? LastUnapprovedAt = null,
    string? LastUnapprovedByName = null,
    string? LastUnapprovalReason = null);

public sealed record BillSummaryView(
    Guid Id,
    string? Number,
    string Status,
    Guid? AppointmentId,
    string CustomerName,
    bool IsWalkIn,
    Guid? StaffProfileId,
    string? StaffName,
    bool StaffIsActive,
    DateOnly ServiceDate,
    int Total,
    int LineCount,
    string? PaymentMethodName,
    DateTime OpenedAt,
    DateTime? ClosedAt,
    bool IsOverdue,
    bool IsSelfApproved,
    bool HasRedemptions);

/// <summary>A completed appointment of the last 30 days with no bill and nothing registered (BUG-092).</summary>
public sealed record BillPendingAppointmentView(
    Guid AppointmentId,
    DateTime StartsAt,
    string CustomerName,
    string? ServiceName,
    Guid StaffProfileId,
    string StaffName);

/// <summary>«Cuentas por cerrar»: open bills (overdue ones flagged, Q5) and completed appointments without a bill.</summary>
public sealed record BillPendingView(
    IReadOnlyList<BillSummaryView> OpenBills,
    IReadOnlyList<BillPendingAppointmentView> AppointmentsWithoutBill,
    int Count);

public sealed record BillCountView(int Count);

public sealed record BillStaffOption(Guid Id, string DisplayName, bool IsActive);

public sealed record BillRateOption(Guid Id, string Name, int Price, int LoyaltyPoints, bool IsActive);

public sealed record BillGeneralServiceOption(Guid Id, string Name, string CategoryName, IReadOnlyList<BillRateOption> Rates);

public sealed record BillProductOption(Guid Id, string Name, int Price, int? AvailableStock);

public sealed record BillPaymentMethodOption(Guid Id, string Name);

/// <summary>Everything the bill forms offer, plus whether the current user is an active professional (and which).</summary>
public sealed record BillOptionsView(
    IReadOnlyList<BillGeneralServiceOption> Services,
    IReadOnlyList<BillProductOption> Products,
    IReadOnlyList<BillStaffOption> Staff,
    IReadOnlyList<BillPaymentMethodOption> PaymentMethods,
    Guid? OwnStaffProfileId,
    bool CanManage,
    bool CanDiscount,
    bool CanCharge,
    bool CanClose,
    bool CanApprove,
    bool CanVoid,
    bool CanAssignOtherStaff = false);

public sealed record BillCustomerOption(Guid Id, string FullName, string? PhoneHint);

public sealed record BillMembershipOption(Guid Id, string PlanName, DateOnly EndsOn, IReadOnlyList<BillMembershipServiceOption> Services);

public sealed record BillMembershipServiceOption(Guid ServiceId, string Name, Guid GeneralServiceId, int Available);

public sealed record BillRedemptionOption(
    Guid Id, string RewardName, string Kind, Guid? ServiceId, string? ServiceName, int PointsCost, DateOnly? DeliverBy);

public sealed record BillEventView(
    Guid Id, string Type, string StatusAtEvent, string? UserName, DateTime CreatedAt, string? Reason, string? Before, string? After);

/// <summary>
/// Filters of the bills list. <c>CustomerId</c> includes the profiles merged into it (ADR-0013); <c>Q</c> matches the number
/// or the customer's name; dates are the service date (at most 30 days).
/// </summary>
public sealed record BillListQuery(
    string? Status = null,
    Guid? StaffProfileId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Q = null,
    int? Page = null,
    int? PageSize = null,
    Guid? CustomerId = null,
    Guid? PaymentMethodId = null);

public sealed record BillOpenRequest(
    Guid IdempotencyKey,
    Guid? AppointmentId = null,
    Guid? CustomerId = null,
    string? CustomerName = null,
    DateOnly? ServiceDate = null,
    Guid? StaffProfileId = null,
    string? Notes = null);

public sealed record BillHeaderRequest(uint? Version, Guid? CustomerId, string? CustomerName, DateOnly? ServiceDate, string? Notes);

/// <summary>
/// One request for every line type; each type reads its own fields. Service: staff, general service, rate (optional
/// while open), courtesy and reason. Product: product, quantity, optional staff. MembershipConsumption: staff, rate,
/// membership. Redemption: redemption and (service rewards) staff. Discount: amount, reason, optional line. Tip: staff,
/// amount, payment mode.
/// </summary>
public sealed record BillLineRequest(
    uint? Version,
    string? Type = null,
    Guid? StaffProfileId = null,
    Guid? GeneralServiceId = null,
    Guid? ServiceId = null,
    Guid? ProductId = null,
    int? Quantity = null,
    Guid? CustomerMembershipId = null,
    Guid? LoyaltyRedemptionId = null,
    int? Amount = null,
    string? Reason = null,
    Guid? AppliesToLineId = null,
    string? TipPaymentMode = null,
    bool IsCourtesy = false);

public sealed record BillCloseRequest(uint? Version, Guid? PaymentMethodId = null);

public sealed record BillReasonRequest(uint? Version, string? Reason);

public sealed record BillPaymentMethodRequest(uint? Version, Guid? PaymentMethodId);

/// <summary>Approve the whole bill. <c>NoPointsLineIds</c>: eligible service lines the approver decides not to credit.</summary>
public sealed record BillApproveRequest(uint? Version, Guid IdempotencyKey, IReadOnlyList<Guid>? NoPointsLineIds = null, string? Notes = null);

/// <summary>ADR-0020 stage 4: unapprove the approved bill; every posting of its approval is reversed. The reason is required.</summary>
public sealed record BillUnapproveRequest(uint? Version, Guid IdempotencyKey, string? Reason);

public sealed record BillApprovalLinePreview(
    Guid LineId,
    string Type,
    string Name,
    string? StaffName,
    bool StaffIsActive,
    int Gross,
    int DiscountShare,
    int Net,
    int ProfessionalAmount,
    int BusinessAmount,
    int? EligiblePoints,
    int? CatalogPriceNow,
    bool PriceChanged,
    string Effect);

/// <summary>What approving would post, with the warnings the approver should see first.</summary>
public sealed record BillApprovalPreviewView(
    Guid BillId,
    uint Version,
    IReadOnlyList<BillApprovalLinePreview> Lines,
    bool WouldBeSelfApproved,
    bool NeedsDeliverPermission,
    bool LoyaltyEnabled,
    IReadOnlyList<string> Warnings);

public sealed record PaymentMethodView(Guid Id, string Name, int SortOrder, bool IsActive);

public sealed record PaymentMethodUpsertRequest(string? Name, int? SortOrder = null, bool? IsActive = null);

public interface IBillsService
{
  Task<BillOptionsView> GetOptionsAsync(Guid currentUserId, CancellationToken cancellationToken = default);

  Task<PagedResult<BillSummaryView>> ListAsync(Guid currentUserId, BillListQuery query, CancellationToken cancellationToken = default);

  Task<BillPendingView> GetPendingAsync(Guid currentUserId, CancellationToken cancellationToken = default);

  Task<BillCountView> GetPendingCountAsync(Guid currentUserId, CancellationToken cancellationToken = default);

  Task<BillView> GetAsync(Guid currentUserId, Guid billId, CancellationToken cancellationToken = default);

  Task<IReadOnlyList<BillEventView>> GetEventsAsync(Guid currentUserId, Guid billId, CancellationToken cancellationToken = default);

  Task<IReadOnlyList<BillCustomerOption>> SearchCustomersAsync(string? query, CancellationToken cancellationToken = default);

  Task<IReadOnlyList<BillMembershipOption>> GetMembershipOptionsAsync(Guid currentUserId, Guid billId, CancellationToken cancellationToken = default);

  Task<IReadOnlyList<BillRedemptionOption>> GetRedemptionOptionsAsync(Guid currentUserId, Guid billId, CancellationToken cancellationToken = default);

  Task<BillView> OpenAsync(Guid currentUserId, BillOpenRequest request, CancellationToken cancellationToken = default);

  Task<BillView> UpdateHeaderAsync(Guid currentUserId, Guid billId, BillHeaderRequest request, CancellationToken cancellationToken = default);

  Task<BillView> AddLineAsync(Guid currentUserId, Guid billId, BillLineRequest request, CancellationToken cancellationToken = default);

  Task<BillView> UpdateLineAsync(Guid currentUserId, Guid billId, Guid lineId, BillLineRequest request, CancellationToken cancellationToken = default);

  Task<BillView> RemoveLineAsync(Guid currentUserId, Guid billId, Guid lineId, uint? version, CancellationToken cancellationToken = default);

  Task<BillView> CloseAsync(Guid currentUserId, Guid billId, BillCloseRequest request, CancellationToken cancellationToken = default);

  Task<BillView> DetachAppointmentAsync(Guid currentUserId, Guid billId, BillReasonRequest request, CancellationToken cancellationToken = default);

  Task<BillView> VoidAsync(Guid currentUserId, Guid billId, BillReasonRequest request, CancellationToken cancellationToken = default);

  Task<BillView> ChangePaymentMethodAsync(Guid currentUserId, Guid billId, BillPaymentMethodRequest request, CancellationToken cancellationToken = default);
}

public interface IBillApprovalService
{
  Task<BillApprovalPreviewView> PreviewAsync(Guid currentUserId, Guid billId, CancellationToken cancellationToken = default);

  Task<BillView> ApproveAsync(Guid currentUserId, Guid billId, BillApproveRequest request, CancellationToken cancellationToken = default);

  Task<BillView> UnapproveAsync(Guid currentUserId, Guid billId, BillUnapproveRequest request, CancellationToken cancellationToken = default);
}

public interface IPaymentMethodsService
{
  Task<IReadOnlyList<PaymentMethodView>> ListAsync(CancellationToken cancellationToken = default);

  Task<PaymentMethodView> CreateAsync(PaymentMethodUpsertRequest request, CancellationToken cancellationToken = default);

  Task<PaymentMethodView> UpdateAsync(Guid paymentMethodId, PaymentMethodUpsertRequest request, CancellationToken cancellationToken = default);
}

// ── ADR-0020 stage 5: the customer's receipts (Q23) and the cash closing ─────────────────────────

/// <summary>Status the customer sees: Issued (closed or approved), UnderReview (unapproved, being corrected) or Voided.</summary>
public sealed record MyBillSummaryView(
    Guid Id, string Number, string Status, DateOnly ServiceDate, DateTime ClosedAt, int Total, string? PaymentMethodName);

/// <summary>A line as the customer sees it: no commissions, costs or internal reasons.</summary>
public sealed record MyBillLineView(
    string Type, string Name, int Quantity, int UnitPrice, int Amount, bool IsCourtesy, string? StaffName, string? TipPaymentMode);

public sealed record MyBillView(
    Guid Id,
    string Number,
    string Status,
    DateOnly ServiceDate,
    DateTime ClosedAt,
    string CustomerName,
    IReadOnlyList<MyBillLineView> Lines,
    int Subtotal,
    int DiscountTotal,
    int TipTotal,
    int DirectTipTotal,
    int Total,
    string? PaymentMethodName);

public sealed record MyBillListQuery(int? Page = null, int? PageSize = null);

public sealed record CashClosingMethodView(Guid PaymentMethodId, string Name, int BillCount, long Total);

public sealed record CashClosingDayView(DateOnly Day, IReadOnlyList<CashClosingMethodView> Methods, int BillCount, long Total);

/// <summary>
/// ADR-0020 stage 5: what was charged, by the Bogotá day the bill was closed and its payment method (P6). Money the
/// customer paid: a later unapproval corrects the incomes, not the cash, so it does not change this. Voided bills
/// that had been charged are shown apart.
/// </summary>
public sealed record CashClosingView(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<CashClosingDayView> Days,
    IReadOnlyList<CashClosingMethodView> Methods,
    int BillCount,
    long Total,
    int VoidedCount,
    long VoidedTotal);

public sealed record CashClosingQuery(DateOnly? From = null, DateOnly? To = null);

public interface IMyBillsService
{
  Task<PagedResult<MyBillSummaryView>> GetMineAsync(Guid currentUserId, MyBillListQuery query, CancellationToken cancellationToken = default);

  Task<MyBillView> GetMineByIdAsync(Guid currentUserId, Guid billId, CancellationToken cancellationToken = default);
}

public interface IBillReportsService
{
  Task<CashClosingView> GetCashClosingAsync(CashClosingQuery query, CancellationToken cancellationToken = default);
}
