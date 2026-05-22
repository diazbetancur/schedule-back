namespace Barbershop.Application.Media;

public interface IMediaAssetsService
{
  Task<MediaAssetView> UploadAsync(
      Guid currentUserId,
      IReadOnlyCollection<string> roles,
      MediaAssetUploadRequest request,
      CancellationToken cancellationToken = default);

  Task<IReadOnlyList<MediaAssetView>> GetAllAsync(CancellationToken cancellationToken = default);

  Task<MediaAssetView> GetByIdAsync(Guid mediaAssetId, CancellationToken cancellationToken = default);

  Task DeleteAsync(Guid mediaAssetId, CancellationToken cancellationToken = default);
}
