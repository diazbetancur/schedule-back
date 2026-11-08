using System.Security.Claims;
using Api.Barbershop.Features.Auth;
using Api.Barbershop.Features.Uploads;
using Barbershop.Application.Auth;
using Barbershop.Application.Common.Paging;
using Barbershop.Application.Products;

namespace Api.Barbershop.Features.Admin.Products;

/// <summary>
/// Masters of product categories and products (ADR-0016 B5). Same policy as the service catalog: the Admin role. The
/// catalog permissions proposed in ADR-0016 T13 (catalog.products.manage) are still pending.
/// </summary>
public static class AdminProductCatalogEndpoints
{
    private const long MaxImageBytes = 10_485_760; // 10 MB, same as the staff photos

    public static RouteGroupBuilder MapAdminProductCatalogEndpoints(this RouteGroupBuilder api)
    {
        var categories = api.MapGroup("/admin/product-categories")
            .WithTags("Admin")
            .RequireAuthorization(AuthPolicyNames.Admin);

        categories.MapGet(string.Empty, (IAdminProductCatalogService service, CancellationToken ct) => service.GetCategoriesAsync(ct))
            .WithName("GetAdminProductCategories")
            .Produces<IReadOnlyList<ProductCategoryView>>(StatusCodes.Status200OK);

        categories.MapGet("/{categoryId:guid}", (Guid categoryId, IAdminProductCatalogService service, CancellationToken ct)
                => service.GetCategoryAsync(categoryId, ct))
            .WithName("GetAdminProductCategoryById")
            .Produces<ProductCategoryView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        categories.MapPost(string.Empty, async (ProductCategoryCreateRequest request, IAdminProductCatalogService service, CancellationToken ct) =>
            {
                var created = await service.CreateCategoryAsync(request, ct);
                return Results.Created($"/api/v1/admin/product-categories/{created.Id}", created);
            })
            .WithName("CreateAdminProductCategory")
            .Produces<ProductCategoryView>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        categories.MapPut("/{categoryId:guid}", (Guid categoryId, ProductCategoryUpdateRequest request, IAdminProductCatalogService service, CancellationToken ct)
                => service.UpdateCategoryAsync(categoryId, request, ct))
            .WithName("UpdateAdminProductCategory")
            .Produces<ProductCategoryView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        categories.MapPatch("/{categoryId:guid}/status", (Guid categoryId, ProductCategoryStatusUpdateRequest request, IAdminProductCatalogService service, CancellationToken ct)
                => service.UpdateCategoryStatusAsync(categoryId, request, ct))
            .WithName("UpdateAdminProductCategoryStatus")
            .Produces<ProductCategoryView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        categories.MapDelete("/{categoryId:guid}", async (Guid categoryId, IAdminProductCatalogService service, CancellationToken ct) =>
            {
                await service.DeleteCategoryAsync(categoryId, ct);
                return Results.NoContent();
            })
            .WithName("DeleteAdminProductCategory")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        var products = api.MapGroup("/admin/products")
            .WithTags("Admin")
            .RequireAuthorization(AuthPolicyNames.Admin);

        products.MapGet(string.Empty, ([AsParameters] ProductListQuery query, IAdminProductCatalogService service, CancellationToken ct)
                => service.GetProductsAsync(query, ct))
            .WithName("GetAdminProducts")
            .Produces<PagedResult<ProductView>>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        products.MapGet("/{productId:guid}", (Guid productId, IAdminProductCatalogService service, CancellationToken ct)
                => service.GetProductAsync(productId, ct))
            .WithName("GetAdminProductById")
            .Produces<ProductView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        products.MapPost(string.Empty, async (ProductCreateRequest request, IAdminProductCatalogService service, CancellationToken ct) =>
            {
                var created = await service.CreateProductAsync(request, ct);
                return Results.Created($"/api/v1/admin/products/{created.Id}", created);
            })
            .WithName("CreateAdminProduct")
            .Produces<ProductView>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        products.MapPut("/{productId:guid}", (Guid productId, ProductUpdateRequest request, IAdminProductCatalogService service, CancellationToken ct)
                => service.UpdateProductAsync(productId, request, ct))
            .WithName("UpdateAdminProduct")
            .Produces<ProductView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        products.MapPatch("/{productId:guid}/status", (Guid productId, ProductStatusUpdateRequest request, IAdminProductCatalogService service, CancellationToken ct)
                => service.UpdateProductStatusAsync(productId, request, ct))
            .WithName("UpdateAdminProductStatus")
            .Produces<ProductView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        products.MapDelete("/{productId:guid}", async (Guid productId, IAdminProductCatalogService service, CancellationToken ct) =>
            {
                await service.DeleteProductAsync(productId, ct);
                return Results.NoContent();
            })
            .WithName("DeleteAdminProduct")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        products.MapPost("/{productId:guid}/image", UploadImageAsync)
            .WithName("UploadAdminProductImage")
            .Produces<ProductView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .DisableAntiforgery();

        products.MapDelete("/{productId:guid}/image", (Guid productId, IAdminProductCatalogService service, CancellationToken ct)
                => service.RemoveProductImageAsync(productId, ct))
            .WithName("RemoveAdminProductImage")
            .Produces<ProductView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return api;
    }

    /// <summary>Same transport as the staff photos: a raw body with X-Upload-* headers, or a multipart "image" field.</summary>
    private static async Task<IResult> UploadImageAsync(
        Guid productId,
        ClaimsPrincipal user,
        HttpRequest request,
        IAdminProductCatalogService service,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        using var uploadedFile = await MultipartFileReader.ReadSingleFileAsync(
            request,
            "image",
            "admin-product-image",
            MaxImageBytes,
            loggerFactory.CreateLogger("Api.Barbershop.Uploads.AdminProductImage"),
            cancellationToken);
        var result = await service.UploadProductImageAsync(
            productId,
            user.GetRequiredUserId(),
            new ProductImageUploadRequest(uploadedFile.FileName, uploadedFile.ContentType, uploadedFile.Length, uploadedFile.Content),
            cancellationToken);
        return Results.Ok(result);
    }
}
