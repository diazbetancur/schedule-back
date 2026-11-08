using Barbershop.Application.Common.Paging;

namespace Barbershop.Application.Memberships.Admin;

/// <summary>Selling, activating and annulling memberships, and linking them to membership-sale income.</summary>
public interface IAdminMembershipsService
{
    /// <summary>One customer's memberships, paginated, newest sale first (ADR-0016 B7).</summary>
    Task<PagedResult<CustomerMembershipSummaryView>> GetCustomerMembershipsAsync(
        Guid customerId, MembershipListQuery query, CancellationToken cancellationToken = default);

    Task<CustomerMembershipDetailView> GetByIdAsync(Guid membershipId, CancellationToken cancellationToken = default);

    Task<CustomerMembershipDetailView> SellAsync(
        Guid currentUserId,
        Guid customerId,
        MembershipSaleRequest request,
        CancellationToken cancellationToken = default);

    Task<CustomerMembershipDetailView> ActivateAsync(
        Guid currentUserId,
        Guid membershipId,
        MembershipActivateRequest request,
        CancellationToken cancellationToken = default);

    Task<CustomerMembershipDetailView> AnnulAsync(
        Guid currentUserId,
        Guid membershipId,
        MembershipAnnulRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MembershipIncomeCandidateView>> GetIncomeCandidatesAsync(DateOnly date, CancellationToken cancellationToken = default);

    Task<CustomerMembershipDetailView> LinkIncomeAsync(
        Guid currentUserId,
        Guid membershipId,
        MembershipIncomeLinkRequest request,
        CancellationToken cancellationToken = default);

    Task<CustomerMembershipDetailView> UnlinkIncomeAsync(
        Guid currentUserId,
        Guid membershipId,
        MembershipIncomeUnlinkRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Filters of the membership lists (ADR-0016 B7), for the admin list of one customer and the customer's own list.
/// <c>Status</c> is the displayed status: PendingActivation, Scheduled, Active, Expired or Annulled. <c>PlanId</c> is
/// the plan sold; <c>SoldFrom</c>/<c>SoldTo</c> an inclusive range of the sale date.
/// </summary>
public sealed record MembershipListQuery(
    string? Status = null,
    Guid? PlanId = null,
    DateOnly? SoldFrom = null,
    DateOnly? SoldTo = null,
    int? Page = null,
    int? PageSize = null);

public static class MembershipDisplayStatuses
{
    public const string PendingActivation = "PendingActivation";
    public const string Scheduled = "Scheduled";
    public const string Active = "Active";
    public const string Expired = "Expired";
    public const string Annulled = "Annulled";

    public static readonly string[] All = [PendingActivation, Scheduled, Active, Expired, Annulled];
}
