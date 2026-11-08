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

  /// <summary>
  /// An appointment for one general service (ADR-0016 D7): it stores the service, its name and the duration reserved,
  /// and ends exactly that long after it starts (D11: later changes to the offering never move it).
  /// </summary>
  public static Appointment ForService(
      Guid staffProfileId,
      string customerName,
      DateTime startsAt,
      AppointmentServiceReservation service,
      AppointmentStatus status,
      AppointmentSource source,
      DateTime createdAt,
      Guid? customerUserId = null,
      string? customerEmail = null,
      string? customerPhone = null,
      string? notes = null)
  {
    ArgumentNullException.ThrowIfNull(service);
    var normalizedStartsAt = DomainValidation.EnsureUtc(startsAt, nameof(startsAt));
    var appointment = new Appointment(
        staffProfileId,
        customerName,
        normalizedStartsAt,
        normalizedStartsAt.AddMinutes(service.EnsureValid().DurationMinutes),
        status,
        source,
        createdAt,
        customerUserId,
        customerEmail,
        customerPhone,
        notes);
    appointment.ApplyService(service);
    return appointment;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid StaffProfileId { get; private set; }

  /// <summary>The general service booked; null on appointments from before services were booked (never backfilled).</summary>
  public Guid? GeneralServiceId { get; private set; }
  public string? GeneralServiceNameSnapshot { get; private set; }

  /// <summary>Minutes reserved when it was booked or last edited; null on appointments from before (use EndsAt − StartsAt).</summary>
  public int? ReservedDurationMinutes { get; private set; }
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

    if (ReservedDurationMinutes is { } reserved && normalizedEndsAt != normalizedStartsAt.AddMinutes(reserved))
    {
      throw new ArgumentException("An appointment booked for a service keeps its reserved duration.", nameof(endsAt));
    }

    CustomerName = DomainValidation.Required(customerName, nameof(customerName), 120, 2);
    CustomerEmail = DomainValidation.OptionalEmail(customerEmail, nameof(customerEmail));
    CustomerPhone = DomainValidation.Optional(customerPhone, 40);
    StartsAt = normalizedStartsAt;
    EndsAt = normalizedEndsAt;
    Notes = DomainValidation.Optional(notes, 2000);
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  /// <summary>Length of the appointment: the reserved duration, or EndsAt − StartsAt for an appointment from before.</summary>
  public int DurationMinutes => ReservedDurationMinutes ?? (int)Math.Round((EndsAt - StartsAt).TotalMinutes);

  /// <summary>
  /// Edits the booking. With <paramref name="service"/> the appointment is (re)assigned that service and reserves its
  /// duration; without it, it keeps its service (or keeps having none) and lasts <paramref name="durationMinutes"/>.
  /// </summary>
  public void UpdateBooking(
      string customerName,
      string? customerEmail,
      string? customerPhone,
      DateTime startsAt,
      int durationMinutes,
      AppointmentServiceReservation? service,
      string? notes,
      DateTime updatedAt)
  {
    var normalizedStartsAt = DomainValidation.EnsureUtc(startsAt, nameof(startsAt));
    var duration = AppointmentServiceReservation.EnsureDuration(durationMinutes, nameof(durationMinutes));
    if (service is not null)
    {
      ApplyService(service with { DurationMinutes = duration });
    }
    else if (GeneralServiceId is not null)
    {
      ReservedDurationMinutes = duration;
    }

    CustomerName = DomainValidation.Required(customerName, nameof(customerName), 120, 2);
    CustomerEmail = DomainValidation.OptionalEmail(customerEmail, nameof(customerEmail));
    CustomerPhone = DomainValidation.Optional(customerPhone, 40);
    StartsAt = normalizedStartsAt;
    EndsAt = normalizedStartsAt.AddMinutes(duration);
    Notes = DomainValidation.Optional(notes, 2000);
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  /// <summary>Moves the appointment keeping its service and its length (D11).</summary>
  public void Reschedule(DateTime startsAt, DateTime updatedAt)
  {
    var length = ReservedDurationMinutes is { } reserved ? TimeSpan.FromMinutes(reserved) : EndsAt - StartsAt;
    var normalizedStartsAt = DomainValidation.EnsureUtc(startsAt, nameof(startsAt));
    StartsAt = normalizedStartsAt;
    EndsAt = normalizedStartsAt + length;
    UpdatedAt = DomainValidation.EnsureUtc(updatedAt, nameof(updatedAt));
  }

  private void ApplyService(AppointmentServiceReservation service)
  {
    service.EnsureValid();
    GeneralServiceId = service.GeneralServiceId;
    GeneralServiceNameSnapshot = service.NameSnapshot.Trim();
    ReservedDurationMinutes = service.DurationMinutes;
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

/// <summary>The general service an appointment is booked for, its name now, and the minutes reserved (10 to 240).</summary>
public sealed record AppointmentServiceReservation(Guid GeneralServiceId, string NameSnapshot, int DurationMinutes)
{
  public const int MinDurationMinutes = 10;
  public const int MaxDurationMinutes = 240;
  public const int NameMaxLength = 120;

  public static int EnsureDuration(int durationMinutes, string paramName)
      => DomainValidation.EnsureRange(durationMinutes, MinDurationMinutes, MaxDurationMinutes, paramName);

  internal AppointmentServiceReservation EnsureValid()
  {
    if (GeneralServiceId == Guid.Empty)
    {
      throw new ArgumentException("An appointment's service is required.", nameof(GeneralServiceId));
    }

    DomainValidation.Required(NameSnapshot?.Trim(), nameof(NameSnapshot), NameMaxLength, 1);
    EnsureDuration(DurationMinutes, nameof(DurationMinutes));
    return this;
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