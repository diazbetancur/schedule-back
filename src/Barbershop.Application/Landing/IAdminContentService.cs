using Barbershop.Application.PublicContent;

namespace Barbershop.Application.Landing;

public interface IAdminContentService
{
  Task<LandingContentResponse> GetLandingAsync(CancellationToken cancellationToken = default);

  Task<LandingContentResponse> UpsertLandingAsync(UpsertLandingContentRequest request, CancellationToken cancellationToken = default);

  Task<IReadOnlyList<BannerResponse>> GetBannersAsync(CancellationToken cancellationToken = default);

  Task<BannerResponse> GetBannerByIdAsync(Guid bannerId, CancellationToken cancellationToken = default);

  Task<BannerResponse> CreateBannerAsync(CreateBannerRequest request, CancellationToken cancellationToken = default);

  Task<BannerResponse> UpdateBannerAsync(Guid bannerId, UpdateBannerRequest request, CancellationToken cancellationToken = default);

  Task DeleteBannerAsync(Guid bannerId, CancellationToken cancellationToken = default);

  Task<IReadOnlyList<TickerItemResponse>> GetTickerItemsAsync(CancellationToken cancellationToken = default);

  Task<TickerItemResponse> GetTickerItemByIdAsync(Guid tickerItemId, CancellationToken cancellationToken = default);

  Task<TickerItemResponse> CreateTickerItemAsync(CreateTickerItemRequest request, CancellationToken cancellationToken = default);

  Task<TickerItemResponse> UpdateTickerItemAsync(Guid tickerItemId, UpdateTickerItemRequest request, CancellationToken cancellationToken = default);

  Task DeleteTickerItemAsync(Guid tickerItemId, CancellationToken cancellationToken = default);

  Task<BrandingSettingsResponse> GetBrandingAsync(CancellationToken cancellationToken = default);

  Task<BrandingSettingsResponse> UpsertBrandingAsync(UpsertBrandingSettingsRequest request, CancellationToken cancellationToken = default);

  Task<BusinessScheduleResponse> GetBusinessScheduleAsync(CancellationToken cancellationToken = default);

  Task<BusinessScheduleResponse> UpsertBusinessScheduleAsync(UpsertBusinessScheduleRequest request, CancellationToken cancellationToken = default);
}
