using Barbershop.Domain.Common;

namespace Barbershop.Domain.Landing;

public sealed class AppBrandingSettings
{
  private AppBrandingSettings()
  {
  }

  public AppBrandingSettings(string appName, string primaryColor, string secondaryColor, DateTime? updatedAt = null)
  {
    AppName = DomainValidation.Required(appName, nameof(appName), 120, 2);
    PrimaryColor = DomainValidation.EnsureHexColor(primaryColor, nameof(primaryColor));
    SecondaryColor = DomainValidation.EnsureHexColor(secondaryColor, nameof(secondaryColor));
    UpdatedAt = updatedAt.HasValue ? DomainValidation.EnsureUtc(updatedAt.Value, nameof(updatedAt)) : null;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public string AppName { get; private set; } = string.Empty;
  public string PrimaryColor { get; private set; } = string.Empty;
  public string SecondaryColor { get; private set; } = string.Empty;
  public Guid? LogoMediaAssetId { get; private set; }
  public Guid? AppIconMediaAssetId { get; private set; }
  public DateTime? UpdatedAt { get; private set; }

  public void Update(string appName, string primaryColor, string secondaryColor, Guid? logoMediaAssetId, Guid? appIconMediaAssetId, DateTime updatedAt)
  {
    AppName = DomainValidation.Required(appName, nameof(appName), 120, 2);
    PrimaryColor = DomainValidation.EnsureHexColor(primaryColor, nameof(primaryColor));
    SecondaryColor = DomainValidation.EnsureHexColor(secondaryColor, nameof(secondaryColor));
    LogoMediaAssetId = logoMediaAssetId;
    AppIconMediaAssetId = appIconMediaAssetId;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }
}