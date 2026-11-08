using Barbershop.Application.Products;

namespace Api.Barbershop.Features.Public.Products;

/// <summary>
/// The anonymous product catalog (ADR-0016 B6), addressed by immutable public codes. An unknown, inactive or deleted
/// entry is a plain 404 that the public pages show as "No disponible".
/// </summary>
public static class PublicProductCatalogEndpoints
{
    private const string NotAvailable = "El elemento no está disponible.";

    public static RouteGroupBuilder MapPublicProductCatalogEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/public/product-categories", (IPublicProductCatalogService service, CancellationToken ct) => service.GetCategoriesAsync(ct))
            .WithTags("Public")
            .WithName("GetPublicProductCategories")
            .Produces<IReadOnlyList<PublicProductCategoryResponse>>(StatusCodes.Status200OK);

        api.MapGet("/public/product-categories/{categoryCode}", async (string categoryCode, IPublicProductCatalogService service, CancellationToken ct) =>
                await service.GetCategoryAsync(categoryCode, ct) is { } category
                    ? Results.Ok(category)
                    : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Resource not found", detail: NotAvailable))
            .WithTags("Public")
            .WithName("GetPublicProductCategoryByCode")
            .Produces<PublicProductCategoryDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/public/products/{productCode}", async (string productCode, IPublicProductCatalogService service, CancellationToken ct) =>
                await service.GetProductAsync(productCode, ct) is { } product
                    ? Results.Ok(product)
                    : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Resource not found", detail: NotAvailable))
            .WithTags("Public")
            .WithName("GetPublicProductByCode")
            .Produces<PublicProductResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return api;
    }
}
