using Barbershop.Domain.Common;
using Barbershop.Domain.Users;

namespace Barbershop.Domain.Appointments;

public sealed class Review
{
  private Review()
  {
  }

  public Review(Guid appointmentId, Guid customerUserId, int stars, DateTime createdAt, string? comment = null)
  {
    AppointmentId = appointmentId;
    CustomerUserId = customerUserId;
    Stars = DomainValidation.EnsureRange(stars, 1, 5, nameof(stars));
    Comment = DomainValidation.Optional(comment, 2000);
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid AppointmentId { get; private set; }
  public Guid CustomerUserId { get; private set; }
  public int Stars { get; private set; }
  public string? Comment { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public Appointment Appointment { get; private set; } = null!;
  public User CustomerUser { get; private set; } = null!;
}