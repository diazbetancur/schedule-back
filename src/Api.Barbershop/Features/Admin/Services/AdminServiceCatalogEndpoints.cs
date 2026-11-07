using Barbershop.Application.Auth;
using Barbershop.Application.Services.Admin;

namespace Api.Barbershop.Features.Admin.Services;

/// <summary>
/// Masters of service categories and general services (ADR-0016 B1). Same policy as the rates (/admin/services):
/// the Admin role. The catalog permissions proposed in ADR-0016 T13 are not part of B1.
/// </summary>
public static class AdminServiceCatalogEndpoints
{
    public static RouteGroupBuilder MapAdminServiceCatalogEndpoints(this RouteGroupBuilder api)
    {
        var categories = api.MapGroup("/admin/service-categories")
            .WithTags("Admin")
            .RequireAuthorization(AuthPolicyNames.Admin);

        categories.MapGet(string.Empty, (IAdminServiceCatalogService service, CancellationToken ct) => service.GetCategoriesAsync(ct))
            .WithName("GetAdminServiceCategories")
            .Produces<IReadOnlyList<ServiceCategoryView>>(StatusCodes.Status200OK);

        categories.MapGet("/{categoryId:guid}", (Guid categoryId, IAdminServiceCatalogService service, CancellationToken ct)
                => service.GetCategoryAsync(categoryId, ct))
            .WithName("GetAdminServiceCategoryById")
            .Produces<ServiceCategoryView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        categories.MapPost(string.Empty, async (ServiceCategoryCreateRequest request, IAdminServiceCatalogService service, CancellationToken ct) =>
            {
                var created = await service.CreateCategoryAsync(request, ct);
                return Results.Created($"/api/v1/admin/service-categories/{created.Id}", created);
            })
            .WithName("CreateAdminServiceCategory")
            .Produces<ServiceCategoryView>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        categories.MapPut("/{categoryId:guid}", (Guid categoryId, ServiceCategoryUpdateRequest request, IAdminServiceCatalogService service, CancellationToken ct)
                => service.UpdateCategoryAsync(categoryId, request, ct))
            .WithName("UpdateAdminServiceCategory")
            .Produces<ServiceCategoryView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        categories.MapPatch("/{categoryId:guid}/status", (Guid categoryId, ServiceCategoryStatusUpdateRequest request, IAdminServiceCatalogService service, CancellationToken ct)
                => service.UpdateCategoryStatusAsync(categoryId, request, ct))
            .WithName("UpdateAdminServiceCategoryStatus")
            .Produces<ServiceCategoryView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        categories.MapDelete("/{categoryId:guid}", async (Guid categoryId, IAdminServiceCatalogService service, CancellationToken ct) =>
            {
                await service.DeleteCategoryAsync(categoryId, ct);
                return Results.NoContent();
            })
            .WithName("DeleteAdminServiceCategory")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        var generalServices = api.MapGroup("/admin/general-services")
            .WithTags("Admin")
            .RequireAuthorization(AuthPolicyNames.Admin);

        generalServices.MapGet(string.Empty, (Guid? categoryId, IAdminServiceCatalogService service, CancellationToken ct)
                => service.GetGeneralServicesAsync(categoryId, ct))
            .WithName("GetAdminGeneralServices")
            .Produces<IReadOnlyList<GeneralServiceView>>(StatusCodes.Status200OK);

        generalServices.MapGet("/{generalServiceId:guid}", (Guid generalServiceId, IAdminServiceCatalogService service, CancellationToken ct)
                => service.GetGeneralServiceAsync(generalServiceId, ct))
            .WithName("GetAdminGeneralServiceById")
            .Produces<GeneralServiceView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        generalServices.MapPost(string.Empty, async (GeneralServiceCreateRequest request, IAdminServiceCatalogService service, CancellationToken ct) =>
            {
                var created = await service.CreateGeneralServiceAsync(request, ct);
                return Results.Created($"/api/v1/admin/general-services/{created.Id}", created);
            })
            .WithName("CreateAdminGeneralService")
            .Produces<GeneralServiceView>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        generalServices.MapPut("/{generalServiceId:guid}", (Guid generalServiceId, GeneralServiceUpdateRequest request, IAdminServiceCatalogService service, CancellationToken ct)
                => service.UpdateGeneralServiceAsync(generalServiceId, request, ct))
            .WithName("UpdateAdminGeneralService")
            .Produces<GeneralServiceView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        generalServices.MapPatch("/{generalServiceId:guid}/status", (Guid generalServiceId, GeneralServiceStatusUpdateRequest request, IAdminServiceCatalogService service, CancellationToken ct)
                => service.UpdateGeneralServiceStatusAsync(generalServiceId, request, ct))
            .WithName("UpdateAdminGeneralServiceStatus")
            .Produces<GeneralServiceView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        generalServices.MapDelete("/{generalServiceId:guid}", async (Guid generalServiceId, IAdminServiceCatalogService service, CancellationToken ct) =>
            {
                await service.DeleteGeneralServiceAsync(generalServiceId, ct);
                return Results.NoContent();
            })
            .WithName("DeleteAdminGeneralService")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }
}
