namespace Barbershop.Application.PublicContent;

public interface IPublicContentService
{
  Task<LandingContentResponse> GetPublicLandingAsync(CancellationToken cancellationToken = default);

  Task<IReadOnlyList<BannerResponse>> GetPublicBannersAsync(CancellationToken cancellationToken = default);

  Task<IReadOnlyList<TickerItemResponse>> GetPublicTickerItemsAsync(CancellationToken cancellationToken = default);

  Task<BrandingSettingsResponse> GetPublicBrandingAsync(CancellationToken cancellationToken = default);

  Task<BusinessScheduleResponse> GetPublicBusinessScheduleAsync(CancellationToken cancellationToken = default);
}
