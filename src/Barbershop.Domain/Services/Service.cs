using Barbershop.Domain.Common;

namespace Barbershop.Domain.Services;

public sealed class Service
{
  private Service()
  {
  }

  public Service(string name, int basePrice, DateTime createdAt)
  {
    Name = DomainValidation.Required(name, nameof(name), 120, 2);
    BasePrice = DomainValidation.EnsureRange(basePrice, 0, int.MaxValue, nameof(basePrice));
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
    IsActive = true;
    IsDeleted = false;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public string Name { get; private set; } = string.Empty;
  public int BasePrice { get; private set; }
  public bool IsActive { get; private set; }
  public bool IsDeleted { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }

  public void Update(string name, int basePrice, DateTime updatedAt)
  {
    Name = DomainValidation.Required(name, nameof(name), 120, 2);
    BasePrice = DomainValidation.EnsureRange(basePrice, 0, int.MaxValue, nameof(basePrice));
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

  public void MarkDeleted(DateTime updatedAt)
  {
    IsDeleted = true;
    IsActive = false;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }
}
