namespace Barbershop.Application.Memberships.Admin;

/// <summary>Selling, activating and annulling memberships, and linking them to membership-sale income.</summary>
public interface IAdminMembershipsService
{
    Task<IReadOnlyList<CustomerMembershipSummaryView>> GetCustomerMembershipsAsync(Guid customerId, CancellationToken cancellationToken = default);

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
