namespace Barbershop.Application.Customer;

public interface ICustomerProfileService
{
  Task<CustomerProfileView> GetAsync(Guid currentUserId, CancellationToken cancellationToken = default);

  Task<CustomerProfileView> UpdateAsync(Guid currentUserId, CustomerProfileUpdateRequest request, CancellationToken cancellationToken = default);

  Task<CustomerProfileView> UploadPhotoAsync(Guid currentUserId, CustomerPhotoUploadRequest request, CancellationToken cancellationToken = default);

  Task<CustomerProfileView> RemovePhotoAsync(Guid currentUserId, CancellationToken cancellationToken = default);

  Task ChangePasswordAsync(Guid currentUserId, CustomerPasswordChangeRequest request, CancellationToken cancellationToken = default);
}
