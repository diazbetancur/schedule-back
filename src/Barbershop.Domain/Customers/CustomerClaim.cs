using Barbershop.Domain.Common;

namespace Barbershop.Domain.Customers;

/// <summary>
/// A request from a registered account to be linked to its existing (historical) customer profile. The customer only
/// sends identification data; they never choose nor see a profile. An administrator verifies the identity in person
/// and, on approval, links the account to the profile they chose with the usual account-link rules. Decided claims are
/// final; the identification data sent is never changed.
/// </summary>
public sealed class CustomerClaim
{
  public const int MaxNotesLength = 500;
  public const int MinReasonLength = 3;
  public const int MaxReasonLength = 500;

  private CustomerClaim()
  {
  }

  public CustomerClaim(
      Guid userId,
      Guid idempotencyKey,
      string fullName,
      string phone,
      string email,
      string? notes,
      DateTime submittedAt)
  {
    if (idempotencyKey == Guid.Empty)
    {
      throw new ArgumentException("A claim needs an idempotency key.", nameof(idempotencyKey));
    }

    UserId = userId;
    IdempotencyKey = idempotencyKey;
    FullName = DomainValidation.Required(fullName, nameof(fullName), 120, 2);
    Phone = DomainValidation.Required(phone, nameof(phone), 40, 5);
    NormalizedPhone = CustomerProfile.NormalizePhone(Phone);
    Email = DomainValidation.RequiredEmail(email, nameof(email));
    NormalizedEmail = DomainValidation.NormalizeEmail(Email);
    Notes = DomainValidation.Optional(notes, MaxNotesLength);
    SubmittedAt = DomainValidation.EnsureUtc(submittedAt, nameof(submittedAt));
    Status = CustomerClaimStatus.Pending;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();

  /// <summary>The account asking to be linked.</summary>
  public Guid UserId { get; private set; }

  /// <summary>Sent by the client once per intended claim, so a retried request is not stored twice.</summary>
  public Guid IdempotencyKey { get; private set; }

  // Identification data as sent by the customer.
  public string FullName { get; private set; } = string.Empty;
  public string Phone { get; private set; } = string.Empty;
  public string NormalizedPhone { get; private set; } = string.Empty;
  public string Email { get; private set; } = string.Empty;
  public string NormalizedEmail { get; private set; } = string.Empty;
  public string? Notes { get; private set; }
  public DateTime SubmittedAt { get; private set; }

  public CustomerClaimStatus Status { get; private set; }

  /// <summary>The profile the account was linked to on approval.</summary>
  public Guid? ApprovedCustomerId { get; private set; }

  public Guid? DecidedByUserId { get; private set; }
  public DateTime? DecidedAt { get; private set; }

  /// <summary>Required on rejection; optional notes on approval.</summary>
  public string? DecisionNotes { get; private set; }

  /// <summary>Mapped to PostgreSQL's xmin system column; used as an optimistic concurrency token.</summary>
  public uint Version { get; private set; }

  public void Approve(Guid customerId, Guid decidedByUserId, string? notes, DateTime decidedAt)
  {
    EnsurePending();
    ApprovedCustomerId = customerId;
    DecidedByUserId = decidedByUserId;
    DecisionNotes = DomainValidation.Optional(notes, MaxReasonLength);
    DecidedAt = DomainValidation.EnsureUtc(decidedAt, nameof(decidedAt));
    Status = CustomerClaimStatus.Approved;
  }

  public void Reject(string reason, Guid decidedByUserId, DateTime decidedAt)
  {
    EnsurePending();
    DecisionNotes = DomainValidation.Required(reason, nameof(reason), MaxReasonLength, MinReasonLength);
    DecidedByUserId = decidedByUserId;
    DecidedAt = DomainValidation.EnsureUtc(decidedAt, nameof(decidedAt));
    Status = CustomerClaimStatus.Rejected;
  }

  private void EnsurePending()
  {
    if (Status != CustomerClaimStatus.Pending)
    {
      throw new InvalidOperationException($"The claim is {Status}; only pending claims can be decided.");
    }
  }
}

public enum CustomerClaimStatus
{
  Pending = 1,
  Approved = 2,
  Rejected = 3
}
