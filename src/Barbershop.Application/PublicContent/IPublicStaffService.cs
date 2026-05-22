namespace Barbershop.Application.PublicContent;

public interface IPublicStaffService
{
  Task<IReadOnlyList<PublicStaffListItemResponse>> GetPublicStaffAsync(
      string? search = null,
      CancellationToken cancellationToken = default);

  Task<PublicStaffProfileResponse> GetPublicStaffByIdAsync(
      Guid staffProfileId,
      CancellationToken cancellationToken = default);
}
