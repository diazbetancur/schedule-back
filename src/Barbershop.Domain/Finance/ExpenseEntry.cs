using Barbershop.Domain.Common;

namespace Barbershop.Domain.Finance;

public sealed class ExpenseEntry
{
  private ExpenseEntry()
  {
  }

  public ExpenseEntry(
      Guid? fixedExpenseId,
      string name,
      int amount,
      DateOnly occurredOn,
      Guid createdByUserId,
      DateTime createdAt)
  {
    FixedExpenseId = fixedExpenseId;
    Name = DomainValidation.Required(name, nameof(name), 120, 2);
    Amount = DomainValidation.EnsureRange(amount, 0, int.MaxValue, nameof(amount));
    OccurredOn = occurredOn;
    CreatedByUserId = createdByUserId;
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
    IsDeleted = false;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid? FixedExpenseId { get; private set; }
  public string Name { get; private set; } = string.Empty;
  public int Amount { get; private set; }
  public DateOnly OccurredOn { get; private set; }
  public Guid CreatedByUserId { get; private set; }
  public bool IsDeleted { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }

  public void Update(
      Guid? fixedExpenseId,
      string name,
      int amount,
      DateOnly occurredOn,
      DateTime updatedAt)
  {
    FixedExpenseId = fixedExpenseId;
    Name = DomainValidation.Required(name, nameof(name), 120, 2);
    Amount = DomainValidation.EnsureRange(amount, 0, int.MaxValue, nameof(amount));
    OccurredOn = occurredOn;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public void MarkDeleted(DateTime updatedAt)
  {
    IsDeleted = true;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }
}
