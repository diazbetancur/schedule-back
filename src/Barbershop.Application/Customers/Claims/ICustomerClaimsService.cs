namespace Barbershop.Application.Customers.Claims;

/// <summary>The signed-in customer: their own profile and their own claims, nothing else.</summary>
public interface IOwnCustomerProfileService
{
    Task<OwnCustomerStatusView> GetStatusAsync(Guid currentUserId, CancellationToken cancellationToken = default);

    Task<OwnCustomerProfileView> CreateOwnProfileAsync(
        Guid currentUserId, OwnCustomerProfileCreateRequest request, CancellationToken cancellationToken = default);

    Task<OwnCustomerClaimView> SubmitClaimAsync(
        Guid currentUserId, CustomerClaimCreateRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Review of claims under customers.claims.approve.</summary>
public interface IAdminCustomerClaimsService
{
    /// <summary>Pending claims oldest first; decided ones newest first.</summary>
    Task<IReadOnlyList<CustomerClaimView>> ListAsync(string? status, CancellationToken cancellationToken = default);

    Task<CustomerClaimView> GetByIdAsync(Guid claimId, CancellationToken cancellationToken = default);

    /// <summary>Profiles matching the claim's phone or email, or the query when given.</summary>
    Task<IReadOnlyList<CustomerClaimCandidate>> GetCandidatesAsync(Guid claimId, string? query, CancellationToken cancellationToken = default);

    Task<CustomerClaimView> ApproveAsync(
        Guid currentUserId, Guid claimId, CustomerClaimApproveRequest request, CancellationToken cancellationToken = default);

    Task<CustomerClaimView> RejectAsync(
        Guid currentUserId, Guid claimId, CustomerClaimRejectRequest request, CancellationToken cancellationToken = default);
}
