namespace Barbershop.Application.Staff.SelfService;

public sealed record StaffMediaUploadRequest(
    string FileName,
    string ContentType,
    long SizeBytes,
    Stream Content);
