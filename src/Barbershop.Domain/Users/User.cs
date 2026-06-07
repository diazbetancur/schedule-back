using Barbershop.Domain.Appointments;
using Barbershop.Domain.Common;
using Barbershop.Domain.Staff;

namespace Barbershop.Domain.Users;

public sealed class User
{
  private User()
  {
  }

  public User(string fullName, string email, string passwordHash, DateTime createdAt, string? phoneNumber = null)
  {
    FullName = DomainValidation.Required(fullName, nameof(fullName), 120, 2);
    SetEmail(email);
    PasswordHash = DomainValidation.Required(passwordHash, nameof(passwordHash), 1024);
    PhoneNumber = DomainValidation.Optional(phoneNumber, 40);
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
    IsActive = true;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public string FullName { get; private set; } = string.Empty;
  public string Email { get; private set; } = string.Empty;
  public string NormalizedEmail { get; private set; } = string.Empty;
  public string? PhoneNumber { get; private set; }
  public string PasswordHash { get; private set; } = string.Empty;
  public Guid? ProfilePhotoMediaAssetId { get; private set; }
  public DateOnly? DateOfBirth { get; private set; }
  public string? PasswordResetTokenHash { get; private set; }
  public DateTime? PasswordResetTokenExpiresAt { get; private set; }
  public bool IsActive { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }
  public StaffProfile? StaffProfile { get; private set; }
  public ICollection<UserRole> UserRoles { get; } = [];
  public ICollection<RefreshToken> RefreshTokens { get; } = [];
  public ICollection<PushSubscription> PushSubscriptions { get; } = [];
  public ICollection<Appointment> CustomerAppointments { get; } = [];
  public ICollection<Review> Reviews { get; } = [];

  public void SetEmail(string email)
  {
    Email = DomainValidation.RequiredEmail(email, nameof(email));
    NormalizedEmail = DomainValidation.NormalizeEmail(Email);
  }

  public void UpdateProfile(string fullName, string? phoneNumber, Guid? profilePhotoMediaAssetId, DateTime updatedAt)
  {
    FullName = DomainValidation.Required(fullName, nameof(fullName), 120, 2);
    PhoneNumber = DomainValidation.Optional(phoneNumber, 40);
    ProfilePhotoMediaAssetId = profilePhotoMediaAssetId;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public void UpdateCustomerProfile(string fullName, string? phoneNumber, DateOnly? dateOfBirth, DateTime updatedAt)
  {
    FullName = DomainValidation.Required(fullName, nameof(fullName), 120, 2);
    PhoneNumber = DomainValidation.Optional(phoneNumber, 40);
    DateOfBirth = dateOfBirth;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public void SetProfilePhoto(Guid? profilePhotoMediaAssetId, DateTime updatedAt)
  {
    ProfilePhotoMediaAssetId = profilePhotoMediaAssetId;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public void SetPasswordHash(string passwordHash, DateTime updatedAt)
  {
    PasswordHash = DomainValidation.Required(passwordHash, nameof(passwordHash), 1024);
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public void SetPasswordResetToken(string tokenHash, DateTime expiresAt)
  {
    PasswordResetTokenHash = DomainValidation.Required(tokenHash, nameof(tokenHash), 512);
    PasswordResetTokenExpiresAt = DomainValidation.EnsureUtc(expiresAt, nameof(expiresAt));
  }

  public bool ConsumePasswordResetToken(DateTime utcNow)
  {
    if (PasswordResetTokenExpiresAt is null || PasswordResetTokenExpiresAt <= utcNow)
    {
      return false;
    }

    PasswordResetTokenHash = null;
    PasswordResetTokenExpiresAt = null;
    return true;
  }

  public void Activate(DateTime updatedAt)
  {
    IsActive = true;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public void Deactivate(DateTime updatedAt)
  {
    IsActive = false;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }
}