using Barbershop.Domain.Common;

namespace Barbershop.Domain.Services;

/// <summary>
/// Category of the general services customers see (Barbería, Cuidado facial...). Independent from any product
/// category (ADR-0016 D1).
/// </summary>
public sealed class ServiceCategory
{
  public const int NameMinLength = 2;
  public const int NameMaxLength = 80;
  public const int MaxSortOrder = 9999;

  private ServiceCategory()
  {
  }

  public ServiceCategory(string name, int sortOrder, DateTime createdAt, Guid? id = null, string? publicCode = null)
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

  /// <summary>Unique among live categories (see <see cref="ServiceCatalogCode.NormalizeName"/>).</summary>
  public string NormalizedName { get; private set; } = string.Empty;
  public int SortOrder { get; private set; }

  /// <summary>Set once at creation and never changed.</summary>
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

  /// <summary>Logical deletion: the row stays, so whatever referenced it keeps its history.</summary>
  public void MarkDeleted(DateTime updatedAt)
  {
    IsDeleted = true;
    IsActive = false;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }
}
