using Barbershop.Application.PublicContent;

namespace Api.Barbershop.Features.Public.Content;

public static class PublicContentEndpoints
{
  public static RouteGroupBuilder MapPublicContentEndpoints(this RouteGroupBuilder api)
  {
    var content = api.MapGroup("/public/content")
        .WithTags("Public");

    // BUG-101: the public answers carry only what the landing shows (Public* contracts); the admin endpoints keep the
    // full records with file ids, flags and timestamps.
    content.MapGet("/landing", GetLandingAsync)
        .WithName("GetPublicLandingContent")
        .Produces<PublicLandingContentResponse>(StatusCodes.Status200OK);

    content.MapGet("/banners", GetBannersAsync)
        .WithName("GetPublicBanners")
        .Produces<IReadOnlyList<PublicBannerResponse>>(StatusCodes.Status200OK);

    content.MapGet("/ticker-items", GetTickerItemsAsync)
        .WithName("GetPublicTickerItems")
        .Produces<IReadOnlyList<PublicTickerItemResponse>>(StatusCodes.Status200OK);

    content.MapGet("/branding", GetBrandingAsync)
        .WithName("GetPublicBranding")
        .Produces<PublicBrandingResponse>(StatusCodes.Status200OK);

    content.MapGet("/business-hours", GetBusinessScheduleAsync)
        .WithName("GetPublicBusinessSchedule")
        .Produces<BusinessScheduleResponse>(StatusCodes.Status200OK);

    return api;
  }

  private static async Task<PublicLandingContentResponse> GetLandingAsync(IPublicContentService service, CancellationToken cancellationToken)
      => PublicLandingContentResponse.From(await service.GetPublicLandingAsync(cancellationToken));

  private static async Task<IReadOnlyList<PublicBannerResponse>> GetBannersAsync(IPublicContentService service, CancellationToken cancellationToken)
      => (await service.GetPublicBannersAsync(cancellationToken)).Select(PublicBannerResponse.From).ToList();

  private static async Task<IReadOnlyList<PublicTickerItemResponse>> GetTickerItemsAsync(IPublicContentService service, CancellationToken cancellationToken)
      => (await service.GetPublicTickerItemsAsync(cancellationToken)).Select(PublicTickerItemResponse.From).ToList();

  private static async Task<PublicBrandingResponse> GetBrandingAsync(IPublicContentService service, CancellationToken cancellationToken)
      => PublicBrandingResponse.From(await service.GetPublicBrandingAsync(cancellationToken));

  private static Task<BusinessScheduleResponse> GetBusinessScheduleAsync(IPublicContentService service, CancellationToken cancellationToken)
      => service.GetPublicBusinessScheduleAsync(cancellationToken);
}
