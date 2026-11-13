using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Common.Paging;
using Barbershop.Application.Media;
using Barbershop.Application.Products;
using Barbershop.Application.Storage;
using Barbershop.Domain.Media;
using Barbershop.Domain.Products;
using Barbershop.Domain.Services;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Media;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Products;

/// <summary>
/// Masters of product categories and products (ADR-0016 B5), with the rules of the service catalog (B1): both are only
/// deleted logically and never while something live depends on them; a product only joins, moves to or is revived in
/// an active category. Every change runs in a transaction with the locks of <see cref="ProductCatalogLocks"/> and
/// revalidates under them. The product image goes through the media pipeline (any image becomes a JPEG there).
/// </summary>
internal sealed class ProductCatalogManagementService : IAdminProductCatalogService
{
    private const int MaxRelockAttempts = 3;

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly IMediaAssetsService _mediaAssetsService;
    private readonly IFileStorageService _fileStorageService;

    public ProductCatalogManagementService(
        AppDbContext dbContext, TimeProvider timeProvider, IMediaAssetsService mediaAssetsService, IFileStorageService fileStorageService)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _mediaAssetsService = mediaAssetsService;
        _fileStorageService = fileStorageService;
    }

    // ── Categories ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ProductCategoryView>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var categories = await _dbContext.ProductCategories.AsNoTracking()
            .Where(category => !category.IsDeleted)
            .OrderBy(category => category.SortOrder).ThenBy(category => category.Name)
            .ToListAsync(cancellationToken);
        var counts = await CountProductsAsync(categories.Select(category => category.Id).ToArray(), cancellationToken);
        return categories.Select(category => MapCategory(category, counts)).ToArray();
    }

    public async Task<ProductCategoryView> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default)
        => await MapCategoryAsync(await LoadCategoryAsync(categoryId, cancellationToken), cancellationToken);

    public async Task<ProductCategoryView> CreateCategoryAsync(ProductCategoryCreateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateCategory(request.Name, request.SortOrder);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await EnsureCategoryNameIsFreeAsync(request.Name, null, cancellationToken);

        var category = new ProductCategory(request.Name, request.SortOrder, UtcNow());
        _dbContext.ProductCategories.Add(category);
        await _dbContext.SaveProductCatalogAndCommitAsync(transaction, cancellationToken);
        return await MapCategoryAsync(category, cancellationToken);
    }

    public async Task<ProductCategoryView> UpdateCategoryAsync(
        Guid categoryId, ProductCategoryUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateCategory(request.Name, request.SortOrder ?? 0);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _dbContext.LockProductCategoryAsync(categoryId, cancellationToken);
        var category = await LoadCategoryAsync(categoryId, cancellationToken);
        await EnsureCategoryNameIsFreeAsync(request.Name, categoryId, cancellationToken);

        category.Update(request.Name, request.SortOrder ?? category.SortOrder, UtcNow());
        await _dbContext.SaveProductCatalogAndCommitAsync(transaction, cancellationToken);
        return await MapCategoryAsync(category, cancellationToken);
    }

    public async Task<ProductCategoryView> UpdateCategoryStatusAsync(
        Guid categoryId, ProductCategoryStatusUpdateRequest request, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _dbContext.LockProductCategoryAsync(categoryId, cancellationToken);
        var category = await LoadCategoryAsync(categoryId, cancellationToken);
        if (request.IsActive)
        {
            category.Activate(UtcNow());
        }
        else
        {
            // Counted under the exclusive lock: a product created, moved or reactivated meanwhile has either
            // committed already (and is counted) or waits for this transaction and then sees the category inactive.
            var activeProducts = await _dbContext.Products
                .CountAsync(product => product.CategoryId == categoryId && !product.IsDeleted && product.IsActive, cancellationToken);
            if (activeProducts > 0)
            {
                throw new ConflictException(
                    $"La categoría tiene {activeProducts} producto(s) activo(s). Desactívalos o muévelos a otra categoría antes de desactivarla.");
            }

            category.Deactivate(UtcNow());
        }

        await _dbContext.SaveProductCatalogAndCommitAsync(transaction, cancellationToken);
        return await MapCategoryAsync(category, cancellationToken);
    }

    public async Task DeleteCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _dbContext.LockProductCategoryAsync(categoryId, cancellationToken);
        var category = await LoadCategoryAsync(categoryId, cancellationToken);
        var products = await _dbContext.Products
            .CountAsync(product => product.CategoryId == categoryId && !product.IsDeleted, cancellationToken);
        if (products > 0)
        {
            throw new ConflictException(
                $"La categoría tiene {products} producto(s). Muévelos a otra categoría o elimínalos antes de eliminarla.");
        }

        category.MarkDeleted(UtcNow());
        await _dbContext.SaveProductCatalogAndCommitAsync(transaction, cancellationToken);
    }

    // ── Products ──────────────────────────────────────────────────────────────

    public async Task<PagedResult<ProductView>> GetProductsAsync(ProductListQuery listQuery, CancellationToken cancellationToken = default)
    {
        var page = PageRequest.Create(listQuery.Page, listQuery.PageSize);
        var isActive = CatalogStatusFilter.Parse(listQuery.Status);

        var query = _dbContext.Products.AsNoTracking().Where(product => !product.IsDeleted);
        if (listQuery.CategoryId is { } id)
        {
            query = query.Where(product => product.CategoryId == id);
        }

        if (ListFilters.Text(listQuery.Q) is { } text)
        {
            var upper = text.ToUpperInvariant();
            query = query.Where(product => product.Name.ToUpper().Contains(upper));
        }

        if (isActive is { } active)
        {
            query = query.Where(product => product.IsActive == active);
        }

        // Catalog order, computed in SQL so that pages follow it: category order and name, then product order and name.
        var ordered =
            from product in query
            join category in _dbContext.ProductCategories.AsNoTracking() on product.CategoryId equals category.Id
            orderby category.SortOrder, category.Name, product.SortOrder, product.Name, product.Id
            select product;

        return await ordered.ToPagedResultAsync(
            page,
            async (products, ct) =>
            {
                var categories = await LoadCategoryLookupAsync(products.Select(product => product.CategoryId), ct);
                var images = await LoadImageUrlsAsync(products.Select(product => product.ImageMediaAssetId), ct);
                return (IReadOnlyList<ProductView>)products.Select(product => MapProduct(product, categories, images)).ToArray();
            },
            cancellationToken);
    }

    public async Task<ProductView> GetProductAsync(Guid productId, CancellationToken cancellationToken = default)
        => await MapProductAsync(await LoadProductAsync(productId, cancellationToken), cancellationToken);

    public async Task<ProductView> CreateProductAsync(ProductCreateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateProduct(request.CategoryId, request.Name, request.Description, request.Price, request.SortOrder);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _dbContext.ShareProductCategoryAsync(request.CategoryId, cancellationToken);
        await EnsureCategoryCanReceiveAsync(request.CategoryId, cancellationToken);
        await EnsureProductNameIsFreeAsync(request.CategoryId, request.Name, null, cancellationToken);

        var product = new Product(
            request.CategoryId, request.Name, NormalizeDescription(request.Description), request.Price!.Value, request.SortOrder, UtcNow());
        _dbContext.Products.Add(product);
        await _dbContext.SaveProductCatalogAndCommitAsync(transaction, cancellationToken);
        return await MapProductAsync(product, cancellationToken);
    }

    public async Task<ProductView> UpdateProductAsync(Guid productId, ProductUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ValidateProduct(request.CategoryId, request.Name, request.Description, request.Price, request.SortOrder ?? 0);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        // Category before product (the global order); the target is locked even if it does not change.
        await _dbContext.ShareProductCategoryAsync(request.CategoryId, cancellationToken);
        await _dbContext.LockProductAsync(productId, cancellationToken);
        var product = await LoadProductAsync(productId, cancellationToken);
        if (product.CategoryId != request.CategoryId)
        {
            await EnsureCategoryCanReceiveAsync(request.CategoryId, cancellationToken);
        }

        // Checked in the target category, so moving a product where an equivalent name lives is a conflict too.
        await EnsureProductNameIsFreeAsync(request.CategoryId, request.Name, productId, cancellationToken);

        product.Update(
            request.CategoryId, request.Name, NormalizeDescription(request.Description), request.Price!.Value, request.SortOrder ?? product.SortOrder, UtcNow());
        await _dbContext.SaveProductCatalogAndCommitAsync(transaction, cancellationToken);
        return await MapProductAsync(product, cancellationToken);
    }

    public async Task<ProductView> UpdateProductStatusAsync(
        Guid productId, ProductStatusUpdateRequest request, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        Product product;
        if (request.IsActive)
        {
            // Reactivating needs an active category: share-lock it first, then the product, and re-check that the
            // product did not move to another category before its own lock was taken.
            product = await LockProductUnderItsCategoryAsync(productId, cancellationToken);
            var categoryIsActive = await _dbContext.ProductCategories.AsNoTracking()
                .AnyAsync(category => category.Id == product.CategoryId && !category.IsDeleted && category.IsActive, cancellationToken);
            if (!categoryIsActive)
            {
                throw new ConflictException("La categoría del producto está inactiva. Actívala o mueve el producto a otra categoría.");
            }

            product.Activate(UtcNow());
        }
        else
        {
            await _dbContext.LockProductAsync(productId, cancellationToken);
            product = await LoadProductAsync(productId, cancellationToken);
            product.Deactivate(UtcNow());
        }

        await _dbContext.SaveProductCatalogAndCommitAsync(transaction, cancellationToken);
        return await MapProductAsync(product, cancellationToken);
    }

    public async Task DeleteProductAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _dbContext.LockProductAsync(productId, cancellationToken);
        var product = await LoadProductAsync(productId, cancellationToken);
        var imageId = product.ImageMediaAssetId;
        product.MarkDeleted(UtcNow());
        await _dbContext.SaveProductCatalogAndCommitAsync(transaction, cancellationToken);

        // BUG-099: the image of a deleted product is released (no longer a reference) and archived, like a replaced one,
        // unless something else still uses it.
        if (imageId is { } releasedImage
            && !await MediaAssetReferences.IsReferencedAsync(_dbContext, releasedImage, cancellationToken))
        {
            await TryArchiveMediaAssetAsync(releasedImage, "Product deleted", cancellationToken);
        }
    }

    // ── Product image ─────────────────────────────────────────────────────────

    public async Task<ProductView> UploadProductImageAsync(
        Guid productId, Guid uploadedByUserId, ProductImageUploadRequest request, CancellationToken cancellationToken = default)
    {
        // Fail before storing anything when the product does not exist.
        if (!await _dbContext.Products.AsNoTracking().AnyAsync(product => product.Id == productId && !product.IsDeleted, cancellationToken))
        {
            throw new KeyNotFoundException("The product was not found.");
        }

        var media = await _mediaAssetsService.UploadAsync(
            uploadedByUserId,
            [RoleNames.Admin],
            new MediaAssetUploadRequest(request.FileName, request.ContentType, request.SizeBytes, MediaAssetPurpose.ProductImage, request.Content),
            cancellationToken);

        Product product;
        Guid? previousImageId;
        try
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            await _dbContext.LockProductAsync(productId, cancellationToken);
            product = await LoadProductAsync(productId, cancellationToken);
            previousImageId = product.ImageMediaAssetId;
            product.SetImage(media.Id, UtcNow());
            await _dbContext.SaveProductCatalogAndCommitAsync(transaction, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            // Deleted while the file was uploading: the new file is not referenced by anything.
            await TryArchiveMediaAssetAsync(media.Id, "Product deleted while its image was uploading", cancellationToken);
            throw;
        }

        if (previousImageId is { } previous && previous != media.Id)
        {
            await TryArchiveMediaAssetAsync(previous, "Replaced by a new product image", cancellationToken);
        }

        return await MapProductAsync(product, cancellationToken);
    }

    public async Task<ProductView> RemoveProductImageAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        Product product;
        Guid? previousImageId;
        await using (var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            await _dbContext.LockProductAsync(productId, cancellationToken);
            product = await LoadProductAsync(productId, cancellationToken);
            previousImageId = product.ImageMediaAssetId;
            if (previousImageId is null)
            {
                return await MapProductAsync(product, cancellationToken);
            }

            product.SetImage(null, UtcNow());
            await _dbContext.SaveProductCatalogAndCommitAsync(transaction, cancellationToken);
        }

        await TryArchiveMediaAssetAsync(previousImageId.Value, "Removed by admin from the product", cancellationToken);
        return await MapProductAsync(product, cancellationToken);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private async Task<Product> LockProductUnderItsCategoryAsync(Guid productId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxRelockAttempts; attempt++)
        {
            var categoryId = await _dbContext.Products.AsNoTracking()
                .Where(product => product.Id == productId && !product.IsDeleted)
                .Select(product => (Guid?)product.CategoryId)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new KeyNotFoundException("The product was not found.");

            await _dbContext.ShareProductCategoryAsync(categoryId, cancellationToken);
            await _dbContext.LockProductAsync(productId, cancellationToken);
            var product = await LoadProductAsync(productId, cancellationToken);
            if (product.CategoryId == categoryId)
            {
                return product;
            }

            // Moved to another category while waiting: start again with the new one.
            _dbContext.Entry(product).State = EntityState.Detached;
        }

        throw new ConflictException("El producto cambió mientras se procesaba. Recarga e inténtalo de nuevo.");
    }

    private async Task<ProductCategory> LoadCategoryAsync(Guid categoryId, CancellationToken cancellationToken)
        => await _dbContext.ProductCategories
               .SingleOrDefaultAsync(category => category.Id == categoryId && !category.IsDeleted, cancellationToken)
           ?? throw new KeyNotFoundException("The product category was not found.");

    private async Task<Product> LoadProductAsync(Guid productId, CancellationToken cancellationToken)
        => await _dbContext.Products
               .SingleOrDefaultAsync(product => product.Id == productId && !product.IsDeleted, cancellationToken)
           ?? throw new KeyNotFoundException("The product was not found.");

    /// <summary>Read under the caller's lock on the category, so it reflects any committed deactivation or deletion.</summary>
    private async Task EnsureCategoryCanReceiveAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        var category = await _dbContext.ProductCategories.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == categoryId && !candidate.IsDeleted, cancellationToken);
        if (category is null)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>
            {
                ["categoryId"] = ["La categoría no existe."],
            });
        }

        if (!category.IsActive)
        {
            throw new ConflictException("La categoría está inactiva. Actívala antes de asignarle productos.");
        }
    }

    private async Task EnsureCategoryNameIsFreeAsync(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        var normalized = ServiceCatalogCode.NormalizeName(name);
        if (await _dbContext.ProductCategories.AnyAsync(
                category => !category.IsDeleted && category.Id != excludeId && category.NormalizedName == normalized, cancellationToken))
        {
            throw new ConflictException(ProductCatalogLocks.CategoryNameTakenMessage);
        }
    }

    private async Task EnsureProductNameIsFreeAsync(Guid categoryId, string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        var normalized = ServiceCatalogCode.NormalizeName(name);
        if (await _dbContext.Products.AnyAsync(
                product => !product.IsDeleted && product.Id != excludeId && product.CategoryId == categoryId && product.NormalizedName == normalized,
                cancellationToken))
        {
            throw new ConflictException(ProductCatalogLocks.ProductNameTakenMessage);
        }
    }

    private static void ValidateCategory(string? name, int sortOrder)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        ValidateName(name, ProductCategory.NameMinLength, ProductCategory.NameMaxLength, errors);
        ValidateSortOrder(sortOrder, errors);
        ThrowIfAny(errors);
    }

    private static void ValidateProduct(Guid categoryId, string? name, string? description, int? price, int sortOrder)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (categoryId == Guid.Empty)
        {
            errors["categoryId"] = ["Elige una categoría."];
        }

        ValidateName(name, Product.NameMinLength, Product.NameMaxLength, errors);
        if (description is not null && description.Trim().Length > Product.DescriptionMaxLength)
        {
            errors["description"] = [$"La descripción admite hasta {Product.DescriptionMaxLength} caracteres."];
        }

        if (price is null or < 0)
        {
            errors["price"] = ["Indica el precio en pesos: un número entero igual o mayor que 0."];
        }

        ValidateSortOrder(sortOrder, errors);
        ThrowIfAny(errors);
    }

    private static void ValidateName(string? name, int minLength, int maxLength, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < minLength || name.Trim().Length > maxLength)
        {
            errors["name"] = [$"El nombre debe tener entre {minLength} y {maxLength} caracteres."];
        }
    }

    private static void ValidateSortOrder(int sortOrder, Dictionary<string, string[]> errors)
    {
        if (sortOrder is < 0 or > ProductCategory.MaxSortOrder)
        {
            errors["sortOrder"] = [$"El orden debe estar entre 0 y {ProductCategory.MaxSortOrder}."];
        }
    }

    private static void ThrowIfAny(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }
    }

    private static string? NormalizeDescription(string? description)
        => string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    /// <summary>
    /// Archives an image that nothing uses any more and deletes its file; if storage fails, the file is queued for a
    /// later deletion (same as the staff photos).
    /// </summary>
    private async Task TryArchiveMediaAssetAsync(Guid mediaAssetId, string reason, CancellationToken cancellationToken)
    {
        var asset = await _dbContext.MediaAssets.SingleOrDefaultAsync(candidate => candidate.Id == mediaAssetId, cancellationToken);
        if (asset is null || asset.Status != MediaAssetStatus.Ready)
        {
            return;
        }

        try
        {
            await _fileStorageService.DeleteAsync(asset.StorageKey, cancellationToken);
            asset.Archive(UtcNow());
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _dbContext.PendingFileDeletions.Add(new PendingFileDeletion(asset.StorageKey, reason, UtcNow()));
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<Dictionary<Guid, (int Total, int Active)>> CountProductsAsync(Guid[] categoryIds, CancellationToken cancellationToken)
    {
        var rows = await _dbContext.Products.AsNoTracking()
            .Where(product => !product.IsDeleted && categoryIds.Contains(product.CategoryId))
            .GroupBy(product => product.CategoryId)
            .Select(group => new { group.Key, Total = group.Count(), Active = group.Count(product => product.IsActive) })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.Key, row => (row.Total, row.Active));
    }

    private async Task<Dictionary<Guid, ProductCategory>> LoadCategoryLookupAsync(IEnumerable<Guid> categoryIds, CancellationToken cancellationToken)
    {
        var ids = categoryIds.Distinct().ToArray();
        return await _dbContext.ProductCategories.AsNoTracking()
            .Where(category => ids.Contains(category.Id))
            .ToDictionaryAsync(category => category.Id, cancellationToken);
    }

    private async Task<Dictionary<Guid, string?>> LoadImageUrlsAsync(IEnumerable<Guid?> imageIds, CancellationToken cancellationToken)
        => await ProductImageUrls.LoadAsync(_dbContext, _fileStorageService, imageIds, cancellationToken);

    private async Task<ProductCategoryView> MapCategoryAsync(ProductCategory category, CancellationToken cancellationToken)
        => MapCategory(category, await CountProductsAsync([category.Id], cancellationToken));

    private async Task<ProductView> MapProductAsync(Product product, CancellationToken cancellationToken)
        => MapProduct(
            product,
            await LoadCategoryLookupAsync([product.CategoryId], cancellationToken),
            await LoadImageUrlsAsync([product.ImageMediaAssetId], cancellationToken));

    private static ProductCategoryView MapCategory(ProductCategory category, Dictionary<Guid, (int Total, int Active)> counts)
    {
        var (total, active) = counts.GetValueOrDefault(category.Id);
        return new ProductCategoryView(
            category.Id, category.Name, category.SortOrder, category.PublicCode, category.IsActive, total, active,
            category.CreatedAt, category.UpdatedAt);
    }

    private static ProductView MapProduct(
        Product product, Dictionary<Guid, ProductCategory> categories, Dictionary<Guid, string?> images)
    {
        var category = categories.GetValueOrDefault(product.CategoryId);
        return new ProductView(
            product.Id, product.CategoryId, category?.Name ?? string.Empty, category is { IsActive: true, IsDeleted: false },
            product.Name, product.Description, product.Price, product.ImageMediaAssetId,
            product.ImageMediaAssetId is { } imageId ? images.GetValueOrDefault(imageId) : null,
            product.SortOrder, product.PublicCode, product.IsActive, product.CreatedAt, product.UpdatedAt);
    }
}

/// <summary>Public addresses of the product images that are ready to show.</summary>
internal static class ProductImageUrls
{
    public static async Task<Dictionary<Guid, string?>> LoadAsync(
        AppDbContext dbContext, IFileStorageService fileStorageService, IEnumerable<Guid?> imageIds, CancellationToken cancellationToken)
    {
        var ids = imageIds.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var assets = await dbContext.MediaAssets.AsNoTracking()
            .Where(asset => ids.Contains(asset.Id) && asset.Status == MediaAssetStatus.Ready)
            .Select(asset => new { asset.Id, asset.StorageKey, asset.PublicUrl })
            .ToListAsync(cancellationToken);
        return assets.ToDictionary(asset => asset.Id, asset => asset.PublicUrl ?? fileStorageService.GetPublicUrl(asset.StorageKey));
    }
}
