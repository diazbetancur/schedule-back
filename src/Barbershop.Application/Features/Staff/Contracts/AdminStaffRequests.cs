namespace Barbershop.Application.Staff.Admin;

public sealed record AdminStaffCreateRequest(
    string FullName,
    string Email,
    string DisplayName,
    string InitialPassword,
    string? PhoneNumber,
    string? Bio,
    int? DefaultAppointmentDurationMinutes,
    Guid? PhotoMediaAssetId,
    Guid? TipsQrMediaAssetId,
    bool? IsActive);

public sealed record AdminStaffUpdateRequest(
    string FullName,
    string Email,
    string DisplayName,
    string? PhoneNumber,
    string? Bio,
    int? DefaultAppointmentDurationMinutes,
    Guid? PhotoMediaAssetId,
    Guid? TipsQrMediaAssetId,
    bool? IsActive);

public sealed record StaffStatusUpdateRequest(bool IsActive);

public sealed record EnableProfessionalProfileRequest(
    string DisplayName,
    int? DefaultAppointmentDurationMinutes);
