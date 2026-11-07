using Barbershop.Application.Common.Paging;

namespace Barbershop.Application.Products;

// ADR-0016 B5: masters of product categories and products, managed by administration. Products are catalog entries
// only: no inventory, cart, sale or income is attached to them.

public sealed record ProductCategoryView(
    Guid Id,
    string Name,
    int SortOrder,
    string PublicCode,
    bool IsActive,
    int ProductCount,
    int ActiveProductCount,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record ProductCategoryCreateRequest(string Name, int SortOrder = 0);

public sealed record ProductCategoryUpdateRequest(string Name, int SortOrder = 0);

public sealed record ProductCategoryStatusUpdateRequest(bool IsActive);

public sealed record ProductView(
    Guid Id,
    Guid CategoryId,
    string CategoryName,
    bool CategoryIsActive,
    string Name,
    string? Description,
    int Price,
    Guid? ImageMediaAssetId,
    string? ImageUrl,
    int SortOrder,
    string PublicCode,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

/// <summary>The price is nullable only so that a request without it is a validation error instead of a free product.</summary>
public sealed record ProductCreateRequest(Guid CategoryId, string Name, int? Price, string? Description = null, int SortOrder = 0);

/// <summary>The price is nullable only so that a request without it is a validation error instead of a free product.</summary>
public sealed record ProductUpdateRequest(Guid CategoryId, string Name, int? Price, string? Description = null, int SortOrder = 0);

public sealed record ProductStatusUpdateRequest(bool IsActive);

public sealed record ProductImageUploadRequest(string FileName, string ContentType, long SizeBytes, Stream Content);

public interface IAdminProductCatalogService
{
    Task<IReadOnlyList<ProductCategoryView>> GetCategoriesAsync(CancellationToken cancellationToken = default);

    Task<ProductCategoryView> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default);

    Task<ProductCategoryView> CreateCategoryAsync(ProductCategoryCreateRequest request, CancellationToken cancellationToken = default);

    Task<ProductCategoryView> UpdateCategoryAsync(Guid categoryId, ProductCategoryUpdateRequest request, CancellationToken cancellationToken = default);

    Task<ProductCategoryView> UpdateCategoryStatusAsync(Guid categoryId, ProductCategoryStatusUpdateRequest request, CancellationToken cancellationToken = default);

    Task DeleteCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default);

    /// <summary>The admin list of products, paginated in catalog order (category order and name, product order, name, id).</summary>
    Task<PagedResult<ProductView>> GetProductsAsync(ProductListQuery query, CancellationToken cancellationToken = default);

    Task<ProductView> GetProductAsync(Guid productId, CancellationToken cancellationToken = default);

    Task<ProductView> CreateProductAsync(ProductCreateRequest request, CancellationToken cancellationToken = default);

    Task<ProductView> UpdateProductAsync(Guid productId, ProductUpdateRequest request, CancellationToken cancellationToken = default);

    Task<ProductView> UpdateProductStatusAsync(Guid productId, ProductStatusUpdateRequest request, CancellationToken cancellationToken = default);

    Task DeleteProductAsync(Guid productId, CancellationToken cancellationToken = default);

    Task<ProductView> UploadProductImageAsync(
        Guid productId, Guid uploadedByUserId, ProductImageUploadRequest request, CancellationToken cancellationToken = default);

    Task<ProductView> RemoveProductImageAsync(Guid productId, CancellationToken cancellationToken = default);
}

// ADR-0016 B6: the anonymous product catalog. Only what a visitor needs: public codes (stable links), names,
// descriptions, prices and images. No internal ids, sort orders, states or timestamps.

public sealed record PublicProductCategoryResponse(string Code, string Name, int ProductCount);

public sealed record PublicProductSummaryResponse(string Code, string Name, string? Description, int Price, string? ImageUrl);

public sealed record PublicProductCategoryDetailResponse(string Code, string Name, IReadOnlyList<PublicProductSummaryResponse> Products);

public sealed record PublicProductResponse(
    string Code,
    string Name,
    string? Description,
    int Price,
    string? ImageUrl,
    string CategoryCode,
    string CategoryName);

public interface IPublicProductCatalogService
{
    /// <summary>Active categories with at least one active product, in display order.</summary>
    Task<IReadOnlyList<PublicProductCategoryResponse>> GetCategoriesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// An active category and its active products; null when the code is unknown or the category inactive or deleted
    /// (an expected outcome for an old printed link, so it is not an exception).
    /// </summary>
    Task<PublicProductCategoryDetailResponse?> GetCategoryAsync(string categoryCode, CancellationToken cancellationToken = default);

    /// <summary>An active product of an active category; null otherwise (see <see cref="GetCategoryAsync"/>).</summary>
    Task<PublicProductResponse?> GetProductAsync(string productCode, CancellationToken cancellationToken = default);
}

/// <summary>
/// Filters of <c>GET /admin/products</c> (ADR-0016 B7): <c>Q</c> matches part of the product name, <c>CategoryId</c> its
/// category and <c>Status</c> is Active or Inactive.
/// </summary>
public sealed record ProductListQuery(
    string? Q = null,
    Guid? CategoryId = null,
    string? Status = null,
    int? Page = null,
    int? PageSize = null);
