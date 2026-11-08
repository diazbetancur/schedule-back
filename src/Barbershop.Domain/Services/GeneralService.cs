using Barbershop.Domain.Common;

namespace Barbershop.Domain.Services;

/// <summary>
/// What a customer asks for (Corte, Barba...), inside a service category (ADR-0016 D2). The settlement rates
/// (<see cref="Service"/>: price, business share, points) belong to one general service each (D3).
/// </summary>
public sealed class GeneralService
{
  public const int NameMinLength = 2;
  public const int NameMaxLength = 120;
  public const int DescriptionMaxLength = 1000;

  private GeneralService()
  {
  }

  public GeneralService(
      Guid categoryId, string name, string? description, int sortOrder, DateTime createdAt, Guid? id = null, string? publicCode = null)
  {
    Id = id ?? Guid.NewGuid();
    CategoryId = EnsureCategory(categoryId);
    Name = DomainValidation.Required(name?.Trim(), nameof(name), NameMaxLength, NameMinLength);
    NormalizedName = ServiceCatalogCode.NormalizeName(Name);
    Description = DomainValidation.Optional(description, DescriptionMaxLength);
    SortOrder = DomainValidation.EnsureRange(sortOrder, 0, ServiceCategory.MaxSortOrder, nameof(sortOrder));
    PublicCode = ServiceCatalogCode.Ensure(publicCode ?? ServiceCatalogCode.Generate(), nameof(publicCode));
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
    IsActive = true;
  }

  public Guid Id { get; private set; }
  public Guid CategoryId { get; private set; }
  public string Name { get; private set; } = string.Empty;

  /// <summary>
  /// Unique among the live general services of the same category: "Corte" may exist in Barbería and in Peluquería,
  /// but only once in each (see <see cref="ServiceCatalogCode.NormalizeName"/>).
  /// </summary>
  public string NormalizedName { get; private set; } = string.Empty;
  public string? Description { get; private set; }
  public int SortOrder { get; private set; }

  /// <summary>Set once at creation and never changed.</summary>
  public string PublicCode { get; private set; } = string.Empty;
  public bool IsActive { get; private set; }
  public bool IsDeleted { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }

  public void Update(Guid categoryId, string name, string? description, int sortOrder, DateTime updatedAt)
  {
    CategoryId = EnsureCategory(categoryId);
    Name = DomainValidation.Required(name?.Trim(), nameof(name), NameMaxLength, NameMinLength);
    NormalizedName = ServiceCatalogCode.NormalizeName(Name);
    Description = DomainValidation.Optional(description, DescriptionMaxLength);
    SortOrder = DomainValidation.EnsureRange(sortOrder, 0, ServiceCategory.MaxSortOrder, nameof(sortOrder));
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

  /// <summary>Logical deletion: the row stays, so the rates and history that point to it keep their reference.</summary>
  public void MarkDeleted(DateTime updatedAt)
  {
    IsDeleted = true;
    IsActive = false;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  private static Guid EnsureCategory(Guid categoryId)
    => categoryId == Guid.Empty ? throw new ArgumentException("A general service needs a category.", nameof(categoryId)) : categoryId;
}
