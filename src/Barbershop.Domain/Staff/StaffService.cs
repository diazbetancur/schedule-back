using Barbershop.Domain.Common;

namespace Barbershop.Domain.Staff;

public sealed class StaffService
{
  private StaffService()
  {
  }

  public StaffService(Guid staffProfileId, string name, int durationMinutes, decimal? price, DateTime createdAt, string? description = null)
  {
    StaffProfileId = staffProfileId;
    Name = DomainValidation.Required(name, nameof(name), 120, 2);
    Description = DomainValidation.Optional(description, 1000);
    DurationMinutes = DomainValidation.EnsureRange(durationMinutes, 15, 240, nameof(durationMinutes));
    Price = DomainValidation.OptionalNonNegative(price, nameof(price));
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
    IsActive = true;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid StaffProfileId { get; private set; }
  public string Name { get; private set; } = string.Empty;
  public string? Description { get; private set; }
  public int DurationMinutes { get; private set; }
  public decimal? Price { get; private set; }
  public bool IsActive { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }
  public StaffProfile StaffProfile { get; private set; } = null!;

  public void Update(string name, string? description, int durationMinutes, decimal? price, bool isActive, DateTime updatedAt)
  {
    Name = DomainValidation.Required(name, nameof(name), 120, 2);
    Description = DomainValidation.Optional(description, 1000);
    DurationMinutes = DomainValidation.EnsureRange(durationMinutes, 15, 240, nameof(durationMinutes));
    Price = DomainValidation.OptionalNonNegative(price, nameof(price));
    IsActive = isActive;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }
}