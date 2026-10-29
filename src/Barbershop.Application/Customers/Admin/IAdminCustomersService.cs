namespace Barbershop.Application.Customers.Admin;

public interface IAdminCustomersService
{
    Task<IReadOnlyList<CustomerListItem>> SearchAsync(string? query, CancellationToken cancellationToken = default);

    Task<CustomerDetailView> GetByIdAsync(Guid customerId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CustomerDuplicateCandidate>> FindDuplicatesAsync(
        string? email,
        string? phone,
        Guid? excludeCustomerId,
        Guid? excludeUserId = null,
        CancellationToken cancellationToken = default);

    Task<CustomerDetailView> CreateAsync(Guid currentUserId, CustomerCreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Creates a profile from a registered account and links it, atomically.</summary>
    Task<CustomerDetailView> CreateFromAccountAsync(
        Guid currentUserId,
        CustomerCreateFromAccountRequest request,
        CancellationToken cancellationToken = default);

    Task<CustomerDetailView> UpdateAsync(Guid customerId, CustomerUpdateRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CustomerAccountCandidate>> GetAccountCandidatesAsync(string? query, CancellationToken cancellationToken = default);

    Task<CustomerDetailView> LinkAccountAsync(
        Guid currentUserId,
        Guid customerId,
        CustomerLinkAccountRequest request,
        CancellationToken cancellationToken = default);

    Task<CustomerDetailView> UnlinkAccountAsync(
        Guid currentUserId,
        Guid customerId,
        CustomerUnlinkAccountRequest request,
        CancellationToken cancellationToken = default);

    Task<CustomerMergePreview> GetMergePreviewAsync(
        Guid targetCustomerId,
        Guid sourceCustomerId,
        CancellationToken cancellationToken = default);

    Task<CustomerDetailView> MergeAsync(
        Guid currentUserId,
        Guid targetCustomerId,
        CustomerMergeRequest request,
        CancellationToken cancellationToken = default);
}
