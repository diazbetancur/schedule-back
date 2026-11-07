using Barbershop.Application.Products;
using Barbershop.Application.Storage;
using Barbershop.Domain.Services;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Products;

/// <summary>
/// The anonymous product catalog (ADR-0016 B6): only active products of active categories, addressed by their
/// immutable public codes so a printed link survives renames and moves. Inactive, deleted and unknown entries all look
/// the same to a visitor (null → "No disponible").
/// </summary>
internal sealed class PublicProductCatalogService : IPublicProductCatalogService
{
    private readonly AppDbContext _dbContext;
    private readonly IFileStorageService _fileStorageService;

    public PublicProductCatalogService(AppDbContext dbContext, IFileStorageService fileStorageService)
    {
        _dbContext = dbContext;
        _fileStorageService = fileStorageService;
    }

    public async Task<IReadOnlyList<PublicProductCategoryResponse>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        // A category without active products is left out: a visitor never opens an empty shelf.
        var rows = await _dbContext.ProductCategories.AsNoTracking()
            .Where(category => category.IsActive && !category.IsDeleted)
            .Select(category => new
            {
                category.PublicCode,
                category.Name,
                category.SortOrder,
                ProductCount = _dbContext.Products.Count(product =>
                    product.CategoryId == category.Id && product.IsActive && !product.IsDeleted),
            })
            .Where(row => row.ProductCount > 0)
            .ToListAsync(cancellationToken);

        return rows
            .OrderBy(row => row.SortOrder).ThenBy(row => row.Name, StringComparer.CurrentCulture)
            .Select(row => new PublicProductCategoryResponse(row.PublicCode, row.Name, row.ProductCount))
            .ToArray();
    }

    public async Task<PublicProductCategoryDetailResponse?> GetCategoryAsync(string categoryCode, CancellationToken cancellationToken = default)
    {
        categoryCode = NormalizeCode(categoryCode);
        if (!IsWellFormed(categoryCode))
        {
            return null;
        }

        var category = await _dbContext.ProductCategories.AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.PublicCode == categoryCode && candidate.IsActive && !candidate.IsDeleted, cancellationToken);
        if (category is null)
        {
            return null;
        }

        var products = await _dbContext.Products.AsNoTracking()
            .Where(product => product.CategoryId == category.Id && product.IsActive && !product.IsDeleted)
            .ToListAsync(cancellationToken);
        var images = await ProductImageUrls.LoadAsync(
            _dbContext, _fileStorageService, products.Select(product => product.ImageMediaAssetId), cancellationToken);

        return new PublicProductCategoryDetailResponse(
            category.PublicCode,
            category.Name,
            products
                .OrderBy(product => product.SortOrder).ThenBy(product => product.Name, StringComparer.CurrentCulture)
                .Select(product => new PublicProductSummaryResponse(
                    product.PublicCode, product.Name, product.Description, product.Price,
                    product.ImageMediaAssetId is { } imageId ? images.GetValueOrDefault(imageId) : null))
                .ToArray());
    }

    public async Task<PublicProductResponse?> GetProductAsync(string productCode, CancellationToken cancellationToken = default)
    {
        productCode = NormalizeCode(productCode);
        if (!IsWellFormed(productCode))
        {
            return null;
        }

        var row = await (
                from product in _dbContext.Products.AsNoTracking()
                join category in _dbContext.ProductCategories.AsNoTracking() on product.CategoryId equals category.Id
                where product.PublicCode == productCode
                      && product.IsActive && !product.IsDeleted
                      && category.IsActive && !category.IsDeleted
                select new { Product = product, CategoryCode = category.PublicCode, CategoryName = category.Name })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var images = await ProductImageUrls.LoadAsync(_dbContext, _fileStorageService, [row.Product.ImageMediaAssetId], cancellationToken);
        return new PublicProductResponse(
            row.Product.PublicCode,
            row.Product.Name,
            row.Product.Description,
            row.Product.Price,
            row.Product.ImageMediaAssetId is { } imageId ? images.GetValueOrDefault(imageId) : null,
            row.CategoryCode,
            row.CategoryName);
    }

    /// <summary>Codes are generated in lower case; a link typed by hand in capitals still works.</summary>
    private static string NormalizeCode(string? code) => code?.Trim().ToLowerInvariant() ?? string.Empty;

    /// <summary>A code that cannot exist is answered without querying (and without logging an error).</summary>
    private static bool IsWellFormed(string code)
        => code.Length is >= ServiceCatalogCode.MinLength and <= ServiceCatalogCode.MaxLength
           && code.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9'));
}
