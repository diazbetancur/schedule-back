using Barbershop.Domain.Common;

namespace Barbershop.Domain.Customers;

/// <summary>
/// A customer profile managed by the admin. It can exist without an app account and be linked
/// to a registered <see cref="Users.User"/> later; memberships hang off this profile, not the user.
/// </summary>
public sealed class CustomerProfile
{
  private CustomerProfile()
  {
  }

  public CustomerProfile(string fullName, string phone, string email, Guid createdByUserId, DateTime createdAt)
  {
    SetDetails(fullName, phone, email);
    CreatedByUserId = createdByUserId;
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public string FullName { get; private set; } = string.Empty;
  public string Phone { get; private set; } = string.Empty;
  public string NormalizedPhone { get; private set; } = string.Empty;
  public string Email { get; private set; } = string.Empty;
  public string NormalizedEmail { get; private set; } = string.Empty;
  public Guid? UserId { get; private set; }
  public DateTime? LinkedAt { get; private set; }
  public Guid? LinkedByUserId { get; private set; }
  public Guid? MergedIntoCustomerId { get; private set; }
  public DateTime? MergedAt { get; private set; }
  public Guid? MergedByUserId { get; private set; }
  public Guid CreatedByUserId { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }

  /// <summary>Mapped to PostgreSQL's xmin system column; used as an optimistic concurrency token.</summary>
  public uint Version { get; private set; }

  public bool IsMerged => MergedIntoCustomerId is not null;

  public void UpdateDetails(string fullName, string phone, string email, DateTime updatedAt)
  {
    EnsureNotMerged();
    SetDetails(fullName, phone, email);
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public void LinkAccount(Guid userId, Guid linkedByUserId, DateTime linkedAt)
  {
    EnsureNotMerged();
    if (UserId is not null)
    {
      throw new InvalidOperationException("The customer already has a linked account.");
    }

    UserId = userId;
    LinkedByUserId = linkedByUserId;
    LinkedAt = DomainValidation.EnsureUtc(linkedAt, nameof(linkedAt));
    UpdatedAt = LinkedAt;
  }

  public Guid UnlinkAccount(DateTime unlinkedAt)
  {
    var userId = UserId ?? throw new InvalidOperationException("The customer has no linked account.");

    UserId = null;
    LinkedByUserId = null;
    LinkedAt = null;
    UpdatedAt = DomainValidation.EnsureUtc(unlinkedAt, nameof(unlinkedAt));
    return userId;
  }

  public void MarkMergedInto(Guid targetCustomerId, Guid mergedByUserId, DateTime mergedAt)
  {
    EnsureNotMerged();
    if (targetCustomerId == Id)
    {
      throw new InvalidOperationException("A customer cannot be merged into itself.");
    }

    MergedIntoCustomerId = targetCustomerId;
    MergedByUserId = mergedByUserId;
    MergedAt = DomainValidation.EnsureUtc(mergedAt, nameof(mergedAt));
    UpdatedAt = MergedAt;
  }

  /// <summary>Bumps the row so concurrent operations on this customer collide on the concurrency token.</summary>
  public void Touch(DateTime updatedAt)
  {
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public static string NormalizeEmail(string email) => DomainValidation.NormalizeEmail(email);

  /// <summary>Digits only, so "+57 300-123 4567" and "573001234567" compare equal.</summary>
  public static string NormalizePhone(string phone) => new(phone.Where(char.IsDigit).ToArray());

  private void SetDetails(string fullName, string phone, string email)
  {
    FullName = DomainValidation.Required(fullName, nameof(fullName), 120, 2);
    Phone = DomainValidation.Required(phone, nameof(phone), 40, 5);
    NormalizedPhone = NormalizePhone(Phone);
    Email = DomainValidation.RequiredEmail(email, nameof(email));
    NormalizedEmail = DomainValidation.NormalizeEmail(Email);
  }

  private void EnsureNotMerged()
  {
    if (IsMerged)
    {
      throw new InvalidOperationException("The customer was merged into another profile.");
    }
  }
}
