using Barbershop.Domain.Appointments;
using Barbershop.Domain.Common;
using Barbershop.Domain.Scheduling;
using Barbershop.Domain.Users;

namespace Barbershop.Domain.Staff;

public sealed class StaffProfile
{
  private StaffProfile()
  {
  }

  public StaffProfile(Guid userId, string displayName, int defaultAppointmentDurationMinutes, DateTime createdAt)
  {
    UserId = userId;
    DisplayName = DomainValidation.Required(displayName, nameof(displayName), 120, 2);
    DefaultAppointmentDurationMinutes = DomainValidation.EnsureRange(defaultAppointmentDurationMinutes, 10, 240, nameof(defaultAppointmentDurationMinutes));
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
    IsActive = true;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid UserId { get; private set; }
  public string DisplayName { get; private set; } = string.Empty;
  public string? Bio { get; private set; }
  public string? PhoneNumber { get; private set; }
  public Guid? PhotoMediaAssetId { get; private set; }
  public Guid? TipsQrMediaAssetId { get; private set; }
  public int DefaultAppointmentDurationMinutes { get; private set; }
  public string? InstagramUrl { get; private set; }
  public string? FacebookUrl { get; private set; }
  public string? TikTokUrl { get; private set; }
  public string? YoutubeUrl { get; private set; }
  public string? XUrl { get; private set; }
  public bool IsActive { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }
  public User User { get; private set; } = null!;
  public ICollection<StaffService> Services { get; } = [];
  public ICollection<StaffAvailabilityRule> AvailabilityRules { get; } = [];
  public ICollection<StaffUnavailablePeriod> UnavailablePeriods { get; } = [];
  public ICollection<Appointment> Appointments { get; } = [];

  public void UpdateDetails(
      string displayName,
      string? bio,
      string? phoneNumber,
      Guid? photoMediaAssetId,
      Guid? tipsQrMediaAssetId,
      int defaultAppointmentDurationMinutes,
      bool isActive,
      DateTime updatedAt,
      string? instagramUrl = null,
      string? facebookUrl = null,
      string? tikTokUrl = null,
      string? youtubeUrl = null,
      string? xUrl = null)
  {
    DisplayName = DomainValidation.Required(displayName, nameof(displayName), 120, 2);
    Bio = DomainValidation.Optional(bio, 2000);
    PhoneNumber = DomainValidation.Optional(phoneNumber, 40);
    PhotoMediaAssetId = photoMediaAssetId;
    TipsQrMediaAssetId = tipsQrMediaAssetId;
    DefaultAppointmentDurationMinutes = DomainValidation.EnsureRange(defaultAppointmentDurationMinutes, 10, 240, nameof(defaultAppointmentDurationMinutes));
    InstagramUrl = DomainValidation.Optional(instagramUrl, 2048);
    FacebookUrl = DomainValidation.Optional(facebookUrl, 2048);
    TikTokUrl = DomainValidation.Optional(tikTokUrl, 2048);
    YoutubeUrl = DomainValidation.Optional(youtubeUrl, 2048);
    XUrl = DomainValidation.Optional(xUrl, 2048);
    IsActive = isActive;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public void SetPhoto(Guid? photoMediaAssetId, DateTime updatedAt)
  {
    PhotoMediaAssetId = photoMediaAssetId;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public void SetTipsQr(Guid? tipsQrMediaAssetId, DateTime updatedAt)
  {
    TipsQrMediaAssetId = tipsQrMediaAssetId;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }
}
