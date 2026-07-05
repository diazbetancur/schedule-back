using Barbershop.Domain.Common;

namespace Barbershop.Domain.Finance;

public sealed class FixedExpense
{
  private FixedExpense()
  {
  }

  public FixedExpense(string name, int? defaultAmount, DateTime createdAt)
  {
    Name = DomainValidation.Required(name, nameof(name), 120, 2);
    DefaultAmount = ValidateDefaultAmount(defaultAmount);
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
    IsActive = true;
    IsDeleted = false;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public string Name { get; private set; } = string.Empty;
  public int? DefaultAmount { get; private set; }
  public bool IsActive { get; private set; }
  public bool IsDeleted { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }

  public void Update(string name, int? defaultAmount, DateTime updatedAt)
  {
    Name = DomainValidation.Required(name, nameof(name), 120, 2);
    DefaultAmount = ValidateDefaultAmount(defaultAmount);
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

  private static int? ValidateDefaultAmount(int? value)
  {
    if (value is null)
    {
      return null;
    }

    if (value < 0)
    {
      throw new ArgumentOutOfRangeException(nameof(value), "DefaultAmount cannot be negative.");
    }

    return value;
  }
}
