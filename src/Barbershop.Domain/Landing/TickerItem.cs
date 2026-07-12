using Barbershop.Domain.Common;

namespace Barbershop.Domain.Landing;

public sealed class TickerItem
{
  private TickerItem()
  {
  }

  public TickerItem(string text, int sortOrder, DateTime createdAt)
  {
    Text = DomainValidation.Required(text, nameof(text), 120, 1);
    SortOrder = sortOrder;
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
    IsActive = true;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public string Text { get; private set; } = string.Empty;
  public int SortOrder { get; private set; }
  public bool IsActive { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }

  public void Update(string text, int sortOrder, bool isActive, DateTime updatedAt)
  {
    Text = DomainValidation.Required(text, nameof(text), 120, 1);
    SortOrder = sortOrder;
    IsActive = isActive;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }
}
