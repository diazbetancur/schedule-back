using Barbershop.Domain.Common;

namespace Barbershop.Domain.Landing;

public sealed class Banner
{
  private Banner()
  {
  }

  public Banner(string title, int sortOrder, DateTime createdAt, string? subtitle = null, Guid? imageMediaAssetId = null, string? linkUrl = null)
  {
    Title = DomainValidation.Required(title, nameof(title), 160, 2);
    Subtitle = DomainValidation.Optional(subtitle, 300);
    ImageMediaAssetId = imageMediaAssetId;
    LinkUrl = DomainValidation.OptionalAbsoluteUriString(linkUrl, nameof(linkUrl));
    SortOrder = sortOrder;
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
    IsActive = true;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public string Title { get; private set; } = string.Empty;
  public string? Subtitle { get; private set; }
  public Guid? ImageMediaAssetId { get; private set; }
  public string? LinkUrl { get; private set; }
  public int SortOrder { get; private set; }
  public bool IsActive { get; private set; }
  public DateTime? StartsAt { get; private set; }
  public DateTime? EndsAt { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }

  public void Update(
      string title,
      string? subtitle,
      Guid? imageMediaAssetId,
      string? linkUrl,
      int sortOrder,
      bool isActive,
      DateTime? startsAt,
      DateTime? endsAt,
      DateTime updatedAt)
  {
    DateTime? normalizedStartsAt = startsAt.HasValue ? DomainValidation.EnsureUtc(startsAt.Value, nameof(startsAt)) : null;
    DateTime? normalizedEndsAt = endsAt.HasValue ? DomainValidation.EnsureUtc(endsAt.Value, nameof(endsAt)) : null;
    if (normalizedStartsAt.HasValue && normalizedEndsAt.HasValue && normalizedEndsAt <= normalizedStartsAt)
    {
      throw new ArgumentException("EndsAt must be after StartsAt when both are provided.", nameof(endsAt));
    }

    Title = DomainValidation.Required(title, nameof(title), 160, 2);
    Subtitle = DomainValidation.Optional(subtitle, 300);
    ImageMediaAssetId = imageMediaAssetId;
    LinkUrl = DomainValidation.OptionalAbsoluteUriString(linkUrl, nameof(linkUrl));
    SortOrder = sortOrder;
    IsActive = isActive;
    StartsAt = normalizedStartsAt;
    EndsAt = normalizedEndsAt;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }
}