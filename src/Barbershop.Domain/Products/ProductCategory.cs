using Barbershop.Domain.Common;
using Barbershop.Domain.Services;

namespace Barbershop.Domain.Products;

/// <summary>
/// Category of the products shown in the public catalog (ADR-0016 B5). Independent from the service categories (D1):
/// the same name may exist in both.
/// </summary>
public sealed class ProductCategory
{
  public const int NameMinLength = 2;
  public const int NameMaxLength = 80;
  public const int MaxSortOrder = 9999;

  private ProductCategory()
  {
  }

  public ProductCategory(string name, int sortOrder, DateTime createdAt, Guid? id = null, string? publicCode = null)
  {
    Id = id ?? Guid.NewGuid();
    Name = DomainValidation.Required(name?.Trim(), nameof(name), NameMaxLength, NameMinLength);
    NormalizedName = ServiceCatalogCode.NormalizeName(Name);
    SortOrder = DomainValidation.EnsureRange(sortOrder, 0, MaxSortOrder, nameof(sortOrder));
    PublicCode = ServiceCatalogCode.Ensure(publicCode ?? ServiceCatalogCode.Generate(), nameof(publicCode));
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
    IsActive = true;
  }

  public Guid Id { get; private set; }
  public string Name { get; private set; } = string.Empty;

  /// <summary>Unique among live product categories (see <see cref="ServiceCatalogCode.NormalizeName"/>).</summary>
  public string NormalizedName { get; private set; } = string.Empty;
  public int SortOrder { get; private set; }

  /// <summary>Set once at creation and never changed: the public catalog links use it.</summary>
  public string PublicCode { get; private set; } = string.Empty;
  public bool IsActive { get; private set; }
  public bool IsDeleted { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }

  public void Update(string name, int sortOrder, DateTime updatedAt)
  {
    Name = DomainValidation.Required(name?.Trim(), nameof(name), NameMaxLength, NameMinLength);
    NormalizedName = ServiceCatalogCode.NormalizeName(Name);
    SortOrder = DomainValidation.EnsureRange(sortOrder, 0, MaxSortOrder, nameof(sortOrder));
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
}
