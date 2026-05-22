namespace Barbershop.Application.Customer;

public sealed record CustomerProfileView(
    Guid Id,
    string FullName,
    string Email,
    string? PhoneNumber,
    DateOnly? DateOfBirth,
    string? PhotoUrl);

public sealed record CustomerProfileUpdateRequest(
    string FullName,
    string? PhoneNumber,
    DateOnly? DateOfBirth);

public sealed record CustomerPasswordChangeRequest(
    string CurrentPassword,
    string NewPassword);

public sealed record CustomerPhotoUploadRequest(
    string FileName,
    string ContentType,
    long SizeBytes,
    Stream Content);
