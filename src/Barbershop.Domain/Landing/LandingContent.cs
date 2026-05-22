using Barbershop.Domain.Common;

namespace Barbershop.Domain.Landing;

public sealed class LandingContent
{
  private LandingContent()
  {
  }

  public LandingContent(string heroTitle, DateTime? updatedAt = null)
  {
    HeroTitle = DomainValidation.Required(heroTitle, nameof(heroTitle), 200, 2);
    UpdatedAt = updatedAt.HasValue ? DomainValidation.EnsureUtc(updatedAt.Value, nameof(updatedAt)) : null;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public string HeroTitle { get; private set; } = string.Empty;
  public string? HeroSubtitle { get; private set; }
  public string? AboutTitle { get; private set; }
  public string? AboutText { get; private set; }
  public string? ContactPhone { get; private set; }
  public string? MapsUrl { get; private set; }
  public string? Address { get; private set; }
  public DateTime? UpdatedAt { get; private set; }

  public void Update(
      string heroTitle,
      string? heroSubtitle,
      string? aboutTitle,
      string? aboutText,
      string? contactPhone,
      string? mapsUrl,
      string? address,
      DateTime updatedAt)
  {
    HeroTitle = DomainValidation.Required(heroTitle, nameof(heroTitle), 200, 2);
    HeroSubtitle = DomainValidation.Optional(heroSubtitle, 400);
    AboutTitle = DomainValidation.Optional(aboutTitle, 200);
    AboutText = DomainValidation.Optional(aboutText, 4000);
    ContactPhone = DomainValidation.Optional(contactPhone, 40);
    MapsUrl = DomainValidation.Optional(mapsUrl, 2000);
    Address = DomainValidation.Optional(address, 300);
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }
}
