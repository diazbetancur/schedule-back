using Barbershop.Application.PublicContent;

namespace Api.Barbershop.Features.Public.Content;

public static class PublicContentEndpoints
{
  public static RouteGroupBuilder MapPublicContentEndpoints(this RouteGroupBuilder api)
  {
    var content = api.MapGroup("/public/content")
        .WithTags("Public");

    content.MapGet("/landing", GetLandingAsync)
        .WithName("GetPublicLandingContent")
        .Produces<LandingContentResponse>(StatusCodes.Status200OK);

    content.MapGet("/banners", GetBannersAsync)
        .WithName("GetPublicBanners")
        .Produces<IReadOnlyList<BannerResponse>>(StatusCodes.Status200OK);

    content.MapGet("/branding", GetBrandingAsync)
        .WithName("GetPublicBranding")
        .Produces<BrandingSettingsResponse>(StatusCodes.Status200OK);

    content.MapGet("/business-hours", GetBusinessScheduleAsync)
        .WithName("GetPublicBusinessSchedule")
        .Produces<BusinessScheduleResponse>(StatusCodes.Status200OK);

    return api;
  }

  private static Task<LandingContentResponse> GetLandingAsync(IPublicContentService service, CancellationToken cancellationToken)
      => service.GetPublicLandingAsync(cancellationToken);

  private static Task<IReadOnlyList<BannerResponse>> GetBannersAsync(IPublicContentService service, CancellationToken cancellationToken)
      => service.GetPublicBannersAsync(cancellationToken);

  private static Task<BrandingSettingsResponse> GetBrandingAsync(IPublicContentService service, CancellationToken cancellationToken)
      => service.GetPublicBrandingAsync(cancellationToken);

  private static Task<BusinessScheduleResponse> GetBusinessScheduleAsync(IPublicContentService service, CancellationToken cancellationToken)
      => service.GetPublicBusinessScheduleAsync(cancellationToken);
}
