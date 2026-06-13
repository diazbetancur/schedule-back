using Barbershop.Application.Auth;
using Barbershop.Application.Landing;
using Barbershop.Application.PublicContent;

namespace Api.Barbershop.Features.Admin.Landing;

public static class AdminContentEndpoints
{
  public static RouteGroupBuilder MapAdminContentEndpoints(this RouteGroupBuilder api)
  {
    var content = api.MapGroup("/admin/content")
        .WithTags("Admin")
        .RequireAuthorization(AuthPolicyNames.Admin);

    content.MapGet("/landing", GetLandingAsync)
        .WithName("GetAdminLandingContent")
        .Produces<LandingContentResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    content.MapPut("/landing", UpsertLandingAsync)
        .WithName("UpsertAdminLandingContent")
        .Produces<LandingContentResponse>(StatusCodes.Status200OK)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    content.MapGet("/banners", GetBannersAsync)
        .WithName("GetAdminBanners")
        .Produces<IReadOnlyList<BannerResponse>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    content.MapGet("/banners/{bannerId:guid}", GetBannerByIdAsync)
        .WithName("GetAdminBannerById")
        .Produces<BannerResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    content.MapPost("/banners", CreateBannerAsync)
        .WithName("CreateAdminBanner")
        .Produces<BannerResponse>(StatusCodes.Status201Created)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    content.MapPut("/banners/{bannerId:guid}", UpdateBannerAsync)
        .WithName("UpdateAdminBanner")
        .Produces<BannerResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    content.MapDelete("/banners/{bannerId:guid}", DeleteBannerAsync)
        .WithName("DeleteAdminBanner")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    content.MapGet("/branding", GetBrandingAsync)
        .WithName("GetAdminBranding")
        .Produces<BrandingSettingsResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    content.MapPut("/branding", UpsertBrandingAsync)
        .WithName("UpsertAdminBranding")
        .Produces<BrandingSettingsResponse>(StatusCodes.Status200OK)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    content.MapGet("/business-hours", GetBusinessScheduleAsync)
        .WithName("GetAdminBusinessSchedule")
        .Produces<BusinessScheduleResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    content.MapPut("/business-hours", UpsertBusinessScheduleAsync)
        .WithName("UpsertAdminBusinessSchedule")
        .Produces<BusinessScheduleResponse>(StatusCodes.Status200OK)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    return api;
  }

  private static Task<LandingContentResponse> GetLandingAsync(IAdminContentService service, CancellationToken cancellationToken)
      => service.GetLandingAsync(cancellationToken);

  private static Task<LandingContentResponse> UpsertLandingAsync(
      UpsertLandingContentRequest request,
      IAdminContentService service,
      CancellationToken cancellationToken)
      => service.UpsertLandingAsync(request, cancellationToken);

  private static Task<IReadOnlyList<BannerResponse>> GetBannersAsync(IAdminContentService service, CancellationToken cancellationToken)
      => service.GetBannersAsync(cancellationToken);

  private static Task<BannerResponse> GetBannerByIdAsync(Guid bannerId, IAdminContentService service, CancellationToken cancellationToken)
      => service.GetBannerByIdAsync(bannerId, cancellationToken);

  private static async Task<IResult> CreateBannerAsync(CreateBannerRequest request, IAdminContentService service, CancellationToken cancellationToken)
  {
    var response = await service.CreateBannerAsync(request, cancellationToken);
    return Results.Created($"/api/v1/admin/content/banners/{response.Id}", response);
  }

  private static Task<BannerResponse> UpdateBannerAsync(
      Guid bannerId,
      UpdateBannerRequest request,
      IAdminContentService service,
      CancellationToken cancellationToken)
      => service.UpdateBannerAsync(bannerId, request, cancellationToken);

  private static async Task<IResult> DeleteBannerAsync(Guid bannerId, IAdminContentService service, CancellationToken cancellationToken)
  {
    await service.DeleteBannerAsync(bannerId, cancellationToken);
    return Results.NoContent();
  }

  private static Task<BrandingSettingsResponse> GetBrandingAsync(IAdminContentService service, CancellationToken cancellationToken)
      => service.GetBrandingAsync(cancellationToken);

  private static Task<BrandingSettingsResponse> UpsertBrandingAsync(
      UpsertBrandingSettingsRequest request,
      IAdminContentService service,
      CancellationToken cancellationToken)
      => service.UpsertBrandingAsync(request, cancellationToken);

  private static Task<BusinessScheduleResponse> GetBusinessScheduleAsync(IAdminContentService service, CancellationToken cancellationToken)
      => service.GetBusinessScheduleAsync(cancellationToken);

  private static Task<BusinessScheduleResponse> UpsertBusinessScheduleAsync(
      UpsertBusinessScheduleRequest request,
      IAdminContentService service,
      CancellationToken cancellationToken)
      => service.UpsertBusinessScheduleAsync(request, cancellationToken);
}
