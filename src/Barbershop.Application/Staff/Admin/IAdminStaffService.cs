namespace Barbershop.Application.Staff.Admin;

public interface IAdminStaffService
{
    Task<IReadOnlyList<StaffManagementView>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<StaffManagementView> GetByIdAsync(Guid staffProfileId, CancellationToken cancellationToken = default);

    Task<StaffManagementView> CreateAsync(AdminStaffCreateRequest request, CancellationToken cancellationToken = default);

    Task<StaffManagementView> UpdateAsync(Guid staffProfileId, AdminStaffUpdateRequest request, CancellationToken cancellationToken = default);

    Task<StaffManagementView> UpdateStatusAsync(Guid staffProfileId, StaffStatusUpdateRequest request, CancellationToken cancellationToken = default);

    Task<StaffManagementView> EnableProfessionalForCurrentUserAsync(
        Guid currentUserId,
        EnableProfessionalProfileRequest request,
        CancellationToken cancellationToken = default);
}
