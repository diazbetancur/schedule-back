namespace Barbershop.Application.Staff.SelfService;

public interface IStaffProfileService
{
    Task<StaffManagementView> GetCurrentAsync(Guid currentUserId, CancellationToken cancellationToken = default);

    Task<StaffManagementView> UpdateCurrentAsync(Guid currentUserId, StaffProfileUpdateRequest request, CancellationToken cancellationToken = default);

    Task<StaffManagementView> UploadPhotoAsync(Guid currentUserId, StaffMediaUploadRequest request, CancellationToken cancellationToken = default);

    Task<StaffManagementView> RemovePhotoAsync(Guid currentUserId, CancellationToken cancellationToken = default);

    Task<StaffManagementView> UploadTipsQrAsync(Guid currentUserId, StaffMediaUploadRequest request, CancellationToken cancellationToken = default);

    Task<StaffManagementView> RemoveTipsQrAsync(Guid currentUserId, CancellationToken cancellationToken = default);
}
