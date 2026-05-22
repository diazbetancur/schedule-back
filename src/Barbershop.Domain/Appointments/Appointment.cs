using Barbershop.Domain.Common;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;

namespace Barbershop.Domain.Appointments;

public sealed class Appointment
{
  private Appointment()
  {
  }

  public Appointment(
      Guid staffProfileId,
      string customerName,
      DateTime startsAt,
      DateTime endsAt,
      AppointmentStatus status,
      AppointmentSource source,
      DateTime createdAt,
      Guid? customerUserId = null,
      string? customerEmail = null,
      string? customerPhone = null,
      string? notes = null)
  {
    var normalizedStartsAt = DomainValidation.EnsureUtc(startsAt, nameof(startsAt));
    var normalizedEndsAt = DomainValidation.EnsureUtc(endsAt, nameof(endsAt));
    if (normalizedEndsAt <= normalizedStartsAt)
    {
      throw new ArgumentException("EndsAt must be after StartsAt.", nameof(endsAt));
    }

    StaffProfileId = staffProfileId;
    CustomerUserId = customerUserId;
    CustomerName = DomainValidation.Required(customerName, nameof(customerName), 120, 2);
    CustomerEmail = DomainValidation.OptionalEmail(customerEmail, nameof(customerEmail));
    CustomerPhone = DomainValidation.Optional(customerPhone, 40);
    StartsAt = normalizedStartsAt;
    EndsAt = normalizedEndsAt;
    Status = status;
    Source = source;
    Notes = DomainValidation.Optional(notes, 2000);
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid StaffProfileId { get; private set; }
  public Guid? CustomerUserId { get; private set; }
  public string CustomerName { get; private set; } = string.Empty;
  public string? CustomerEmail { get; private set; }
  public string? CustomerPhone { get; private set; }
  public DateTime StartsAt { get; private set; }
  public DateTime EndsAt { get; private set; }
  public AppointmentStatus Status { get; private set; }
  public AppointmentSource Source { get; private set; }
  public string? Notes { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }
  public StaffProfile StaffProfile { get; private set; } = null!;
  public User? CustomerUser { get; private set; }
  public Review? Review { get; private set; }

  public void UpdateDetails(
      string customerName,
      string? customerEmail,
      string? customerPhone,
      DateTime startsAt,
      DateTime endsAt,
      string? notes,
      DateTime updatedAt)
  {
    var normalizedStartsAt = DomainValidation.EnsureUtc(startsAt, nameof(startsAt));
    var normalizedEndsAt = DomainValidation.EnsureUtc(endsAt, nameof(endsAt));
    if (normalizedEndsAt <= normalizedStartsAt)
    {
      throw new ArgumentException("EndsAt must be after StartsAt.", nameof(endsAt));
    }

    CustomerName = DomainValidation.Required(customerName, nameof(customerName), 120, 2);
    CustomerEmail = DomainValidation.OptionalEmail(customerEmail, nameof(customerEmail));
    CustomerPhone = DomainValidation.Optional(customerPhone, 40);
    StartsAt = normalizedStartsAt;
    EndsAt = normalizedEndsAt;
    Notes = DomainValidation.Optional(notes, 2000);
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public void UpdateStatus(AppointmentStatus status, DateTime updatedAt)
  {
    Status = status;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public void UpdateNotes(string? notes, DateTime updatedAt)
  {
    Notes = DomainValidation.Optional(notes, 2000);
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  public bool Overlaps(DateTime startsAt, DateTime endsAt)
  {
    var normalizedStartsAt = DomainValidation.EnsureUtc(startsAt, nameof(startsAt));
    var normalizedEndsAt = DomainValidation.EnsureUtc(endsAt, nameof(endsAt));
    return StartsAt < normalizedEndsAt && normalizedStartsAt < EndsAt;
  }
}

public enum AppointmentStatus
{
  Pending = 1,
  Confirmed = 2,
  Completed = 3,
  Cancelled = 4,
  NoShow = 5
}

public enum AppointmentSource
{
  CustomerBooking = 1,
  Manual = 2
}