namespace Barbershop.Application.Customers.Admin;

public sealed record CustomerLinkedAccountView(
    Guid UserId,
    string FullName,
    string Email,
    string? PhoneNumber,
    DateTime LinkedAt,
    string? LinkedByName);

public sealed record CustomerListItem(
    Guid Id,
    string FullName,
    string Phone,
    string Email,
    CustomerLinkedAccountView? LinkedAccount,
    DateTime CreatedAt);

public sealed record CustomerEventView(
    Guid Id,
    string Type,
    DateTime PerformedAt,
    string PerformedByName,
    Guid? UserId,
    string? UserEmail,
    Guid? RelatedCustomerId,
    string? RelatedCustomerName,
    string? Reason);

public sealed record CustomerDetailView(
    Guid Id,
    string FullName,
    string Phone,
    string Email,
    CustomerLinkedAccountView? LinkedAccount,
    Guid? MergedIntoCustomerId,
    string? MergedIntoCustomerName,
    DateTime? MergedAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    IReadOnlyList<CustomerEventView> Events);

public sealed record CustomerCreateRequest(string FullName, string Phone, string Email);

public sealed record CustomerUpdateRequest(string FullName, string Phone, string Email);

/// <summary>
/// A possible duplicate of the data being entered. <c>Kind</c> is "customer" or "account".
/// <c>IsLinked</c> means the profile already has an account, or the account already backs a profile.
/// </summary>
public sealed record CustomerDuplicateCandidate(
    string Kind,
    Guid Id,
    string FullName,
    string Email,
    string? Phone,
    IReadOnlyList<string> MatchedOn,
    bool IsLinked);

/// <summary>Name and email are copied from the account; <c>Phone</c> is only needed when the account has none.</summary>
public sealed record CustomerCreateFromAccountRequest(Guid UserId, string? Phone, bool IdentityVerified);

public sealed record CustomerAccountCandidate(
    Guid UserId,
    string FullName,
    string Email,
    string? PhoneNumber,
    DateTime CreatedAt);

public sealed record CustomerLinkAccountRequest(Guid UserId, bool IdentityVerified);

public sealed record CustomerUnlinkAccountRequest(string Reason);

public sealed record CustomerMergeRequest(Guid SourceCustomerId);

public sealed record CustomerMergePreview(
    CustomerListItem Target,
    CustomerListItem Source,
    bool CanMerge,
    IReadOnlyList<string> BlockingReasons,
    IReadOnlyList<string> Warnings,
    CustomerLinkedAccountView? AccountToTransfer);
