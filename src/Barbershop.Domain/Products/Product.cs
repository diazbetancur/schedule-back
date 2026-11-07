using Barbershop.Domain.Common;
using Barbershop.Domain.Services;

namespace Barbershop.Domain.Products;

/// <summary>
/// A product of the public catalog, with its price in whole Colombian pesos (ADR-0016 B5). Only a catalog entry:
/// no inventory, cart, sale or income is attached to it.
/// </summary>
public sealed class Product
{
  public const int NameMinLength = 2;
  public const int NameMaxLength = 120;
  public const int DescriptionMaxLength = 1000;

  private Product()
  {
  }

  public Product(
      Guid categoryId, string name, string? description, int price, int sortOrder, DateTime createdAt,
      Guid? id = null, string? publicCode = null)
  {
    Id = id ?? Guid.NewGuid();
    CategoryId = EnsureCategory(categoryId);
    Name = DomainValidation.Required(name?.Trim(), nameof(name), NameMaxLength, NameMinLength);
    NormalizedName = ServiceCatalogCode.NormalizeName(Name);
    Description = DomainValidation.Optional(description, DescriptionMaxLength);
    Price = DomainValidation.EnsureRange(price, 0, int.MaxValue, nameof(price));
    SortOrder = DomainValidation.EnsureRange(sortOrder, 0, ProductCategory.MaxSortOrder, nameof(sortOrder));
    PublicCode = ServiceCatalogCode.Ensure(publicCode ?? ServiceCatalogCode.Generate(), nameof(publicCode));
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
    IsActive = true;
  }

  public Guid Id { get; private set; }
  public Guid CategoryId { get; private set; }
  public string Name { get; private set; } = string.Empty;

  /// <summary>Unique among the live products of the same category (see <see cref="ServiceCatalogCode.NormalizeName"/>).</summary>
  public string NormalizedName { get; private set; } = string.Empty;
  public string? Description { get; private set; }

  /// <summary>Whole Colombian pesos.</summary>
  public int Price { get; private set; }

  /// <summary>Optional image, uploaded through the media pipeline (always stored as an image).</summary>
  public Guid? ImageMediaAssetId { get; private set; }
  public int SortOrder { get; private set; }

  /// <summary>Set once at creation and never changed: the public product link uses it, so a rename or a move keeps it.</summary>
  public string PublicCode { get; private set; } = string.Empty;
  public bool IsActive { get; private set; }
  public bool IsDeleted { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }

  public void Update(Guid categoryId, string name, string? description, int price, int sortOrder, DateTime updatedAt)
  {
    CategoryId = EnsureCategory(categoryId);
    Name = DomainValidation.Required(name?.Trim(), nameof(name), NameMaxLength, NameMinLength);
    NormalizedName = ServiceCatalogCode.NormalizeName(Name);
    Description = DomainValidation.Optional(description, DescriptionMaxLength);
    Price = DomainValidation.EnsureRange(price, 0, int.MaxValue, nameof(price));
    SortOrder = DomainValidation.EnsureRange(sortOrder, 0, ProductCategory.MaxSortOrder, nameof(sortOrder));
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public void SetImage(Guid? mediaAssetId, DateTime updatedAt)
  {
    ImageMediaAssetId = mediaAssetId == Guid.Empty ? null : mediaAssetId;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
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

  /// <summary>Logical deletion: the row stays, and its public code is never handed out again.</summary>
  public void MarkDeleted(DateTime updatedAt)
  {
    IsDeleted = true;
    IsActive = false;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  private static Guid EnsureCategory(Guid categoryId)
    => categoryId == Guid.Empty ? throw new ArgumentException("A product needs a category.", nameof(categoryId)) : categoryId;
}
