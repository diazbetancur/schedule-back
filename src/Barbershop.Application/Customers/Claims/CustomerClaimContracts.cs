namespace Barbershop.Application.Customers.Claims;

// Status travels as "Pending" | "Approved" | "Rejected".

/// <summary>The signed-in customer's own profile; only their own data.</summary>
public sealed record OwnCustomerProfileView(Guid Id, string FullName, string Phone, string Email, DateTime LinkedAt);

/// <summary>One of the customer's own claims. It never tells which profile was chosen nor anything from it.</summary>
public sealed record OwnCustomerClaimView(
    Guid Id,
    string Status,
    string FullName,
    string Phone,
    string Email,
    string? Notes,
    DateTime SubmittedAt,
    DateTime? DecidedAt,
    string? RejectionReason);

/// <summary>What the customer can do: they have a profile, a claim waiting, or must choose.</summary>
public sealed record OwnCustomerStatusView(
    OwnCustomerProfileView? Profile,
    OwnCustomerClaimView? PendingClaim,
    IReadOnlyList<OwnCustomerClaimView> Claims,
    string AccountFullName,
    string AccountEmail,
    string? AccountPhone);

/// <summary>"I am a new customer": a profile linked to the caller's own account. Retrying returns the same profile.</summary>
public sealed record OwnCustomerProfileCreateRequest(string FullName, string Phone, string Email);

/// <summary>"I am already a customer": identification data for an administrator to find the profile.</summary>
public sealed record CustomerClaimCreateRequest(string FullName, string Phone, string Email, string? Notes, Guid IdempotencyKey);

public sealed record CustomerClaimView(
    Guid Id,
    string Status,
    Guid UserId,
    string AccountFullName,
    string AccountEmail,
    bool AccountIsActive,
    string FullName,
    string Phone,
    string Email,
    string? Notes,
    DateTime SubmittedAt,
    Guid? ApprovedCustomerId,
    string? ApprovedCustomerName,
    DateTime? DecidedAt,
    string? DecidedByName,
    string? DecisionNotes,
    uint Version);

/// <summary>
/// A profile the claim may belong to, reduced to what identifies it (hints only). <c>IsLinked</c> profiles are shown so
/// the reviewer understands why they cannot be chosen. <c>MatchedOn</c> marks coincidences with the claim; they are
/// suggestions, never an automatic link.
/// </summary>
public sealed record CustomerClaimCandidate(
    Guid CustomerId,
    string FullName,
    string PhoneHint,
    string? EmailHint,
    DateTime CreatedAt,
    int MembershipCount,
    bool IsLinked,
    IReadOnlyList<string> MatchedOn);

/// <summary>Links the claim's account to <c>CustomerId</c>; the identity must have been verified in person.</summary>
public sealed record CustomerClaimApproveRequest(uint? Version, Guid CustomerId, bool IdentityVerified, string? Notes);

public sealed record CustomerClaimRejectRequest(uint? Version, string? Reason);
