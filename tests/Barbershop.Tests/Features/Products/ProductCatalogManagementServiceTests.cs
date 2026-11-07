using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Media;
using Barbershop.Application.Products;
using Barbershop.Application.Services.Admin;
using Barbershop.Application.Storage;
using Barbershop.Domain.Media;
using Barbershop.Domain.Services;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Products;
using Barbershop.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Barbershop.Tests.Features.Products;

/// <summary>ADR-0016 B5 (masters of product categories and products) and B6 (the anonymous product catalog).</summary>
public sealed class ProductCatalogManagementServiceTests : IDisposable
{
  private readonly AppDbContext _dbContext;
  private readonly FakeFileStorageService _storage = new();
  private readonly IAdminProductCatalogService _catalog;
  private readonly IPublicProductCatalogService _public;
  private readonly Guid _adminId;

  public ProductCatalogManagementServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options;
    _dbContext = new AppDbContext(options);
    _dbContext.Database.EnsureCreated();
    var admin = new User("Admin", "admin@example.com", "hash", DateTime.UtcNow);
    _dbContext.Users.Add(admin);
    _dbContext.SaveChanges();
    _adminId = admin.Id;
    _catalog = new ProductCatalogManagementService(_dbContext, TimeProvider.System, new FakeMediaAssetsService(_dbContext), _storage);
    _public = new PublicProductCatalogService(_dbContext, _storage);
  }

  public void Dispose() => _dbContext.Dispose();

  // ── Masters ───────────────────────────────────────────────────────────────

  [Fact]
  public async Task Categories_HaveUniqueNames_IgnoringCaseAndSpaces_IndependentFromServiceCategories()
  {
    var hair = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("  Cuidado del cabello ", 1));
    Assert.Equal("Cuidado del cabello", hair.Name);
    Assert.Matches("^[a-z0-9]{8}$", hair.PublicCode);

    await Assert.ThrowsAsync<ConflictException>(() => _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("CUIDADO DEL CABELLO", 2)));
    var other = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("Barba", 2));
    var conflict = await Assert.ThrowsAsync<ConflictException>(() =>
        _catalog.UpdateCategoryAsync(other.Id, new ProductCategoryUpdateRequest(" cuidado del cabello", 2)));
    Assert.Equal("Ya existe una categoría de productos con ese nombre.", conflict.Message);

    // D1: product categories and service categories are separate masters, the same name lives in both.
    var sameAsService = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest(ServiceCatalogDefaults.BarberiaCategoryName, 3));
    Assert.Equal("Barbería", sameAsService.Name);
    var serviceCatalog = new ServiceCatalogManagementService(_dbContext, TimeProvider.System);
    await serviceCatalog.CreateCategoryAsync(new ServiceCategoryCreateRequest("Barba", 4));
  }

  [Fact]
  public async Task Requests_AreValidated_IncludingAMissingOrNegativePrice()
  {
    var category = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("Ceras", 1));

    var badCategory = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("x", 10000)));
    Assert.Equal(["name", "sortOrder"], badCategory.Errors.Keys.Order());

    var missingPrice = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _catalog.CreateProductAsync(new ProductCreateRequest(category.Id, "Cera mate", null)));
    Assert.Equal(["price"], missingPrice.Errors.Keys);

    var badProduct = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _catalog.CreateProductAsync(new ProductCreateRequest(Guid.Empty, " ", -1, new string('a', 1001), -1)));
    Assert.Equal(["categoryId", "description", "name", "price", "sortOrder"], badProduct.Errors.Keys.Order());

    // Free products are allowed: the price only has to be 0 or more.
    var free = await _catalog.CreateProductAsync(new ProductCreateRequest(category.Id, "Muestra", 0));
    Assert.Equal(0, free.Price);
  }

  [Fact]
  public async Task AProduct_NeedsAnExistingActiveCategory()
  {
    var unknown = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _catalog.CreateProductAsync(new ProductCreateRequest(Guid.NewGuid(), "Cera mate", 35000)));
    Assert.Contains("categoryId", unknown.Errors.Keys);

    var paused = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("Pausada", 1));
    await _catalog.UpdateCategoryStatusAsync(paused.Id, new ProductCategoryStatusUpdateRequest(false));
    await Assert.ThrowsAsync<ConflictException>(() =>
        _catalog.CreateProductAsync(new ProductCreateRequest(paused.Id, "Cera mate", 35000)));

    // Nor can an existing product move into it.
    var waxes = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("Ceras", 2));
    var wax = await _catalog.CreateProductAsync(new ProductCreateRequest(waxes.Id, "Cera mate", 35000));
    await Assert.ThrowsAsync<ConflictException>(() =>
        _catalog.UpdateProductAsync(wax.Id, new ProductUpdateRequest(paused.Id, "Cera mate", 35000)));
    Assert.Equal(waxes.Id, (await _catalog.GetProductAsync(wax.Id)).CategoryId);
  }

  [Fact]
  public async Task ProductNames_AreUniquePerCategory_AlsoWhenMoving()
  {
    var waxes = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("Ceras", 1));
    var beard = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("Barba", 2));
    await _catalog.CreateProductAsync(new ProductCreateRequest(waxes.Id, "Cera mate", 35000));

    var conflict = await Assert.ThrowsAsync<ConflictException>(() =>
        _catalog.CreateProductAsync(new ProductCreateRequest(waxes.Id, "  CERA MATE ", 30000)));
    Assert.Equal("Ya existe un producto con ese nombre en esta categoría.", conflict.Message);

    // Same name in another category: allowed. Moving it next to its namesake: refused.
    var other = await _catalog.CreateProductAsync(new ProductCreateRequest(beard.Id, "Cera mate", 30000));
    await Assert.ThrowsAsync<ConflictException>(() =>
        _catalog.UpdateProductAsync(other.Id, new ProductUpdateRequest(waxes.Id, "cera mate", 30000)));
    var moved = await _catalog.UpdateProductAsync(other.Id, new ProductUpdateRequest(waxes.Id, "Cera mate para barba", 30000));
    Assert.Equal((waxes.Id, "Ceras"), (moved.CategoryId, moved.CategoryName));
  }

  [Fact]
  public async Task UpdatingAProduct_KeepsItsIdAndPublicCode()
  {
    var waxes = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("Ceras", 1));
    var oils = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("Aceites", 2));
    var created = await _catalog.CreateProductAsync(new ProductCreateRequest(waxes.Id, "Cera", 35000, " Fijación fuerte ", 3));
    Assert.Equal(("Fijación fuerte", 3), (created.Description, created.SortOrder));

    var updated = await _catalog.UpdateProductAsync(created.Id, new ProductUpdateRequest(oils.Id, "Aceite de barba", 42000, "  ", 1));

    Assert.Equal((created.Id, created.PublicCode, oils.Id, "Aceite de barba", 42000, (string?)null, 1),
        (updated.Id, updated.PublicCode, updated.CategoryId, updated.Name, updated.Price, updated.Description, updated.SortOrder));
    Assert.Equal((0, 0), ((await _catalog.GetCategoryAsync(waxes.Id)).ProductCount, (await _catalog.GetCategoryAsync(waxes.Id)).ActiveProductCount));
    Assert.Equal((1, 1), ((await _catalog.GetCategoryAsync(oils.Id)).ProductCount, (await _catalog.GetCategoryAsync(oils.Id)).ActiveProductCount));
  }

  [Fact]
  public async Task ACategoryWithActiveProducts_CannotBeDeactivated_NorOneWithLiveProductsDeleted()
  {
    var waxes = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("Ceras", 1));
    var wax = await _catalog.CreateProductAsync(new ProductCreateRequest(waxes.Id, "Cera mate", 35000));

    await Assert.ThrowsAsync<ConflictException>(() => _catalog.UpdateCategoryStatusAsync(waxes.Id, new ProductCategoryStatusUpdateRequest(false)));
    await Assert.ThrowsAsync<ConflictException>(() => _catalog.DeleteCategoryAsync(waxes.Id));

    // An inactive product no longer blocks deactivation, but it still blocks deletion.
    await _catalog.UpdateProductStatusAsync(wax.Id, new ProductStatusUpdateRequest(false));
    Assert.False((await _catalog.UpdateCategoryStatusAsync(waxes.Id, new ProductCategoryStatusUpdateRequest(false))).IsActive);
    await Assert.ThrowsAsync<ConflictException>(() => _catalog.DeleteCategoryAsync(waxes.Id));

    // A product of an inactive category cannot be reactivated until its category is.
    await Assert.ThrowsAsync<ConflictException>(() => _catalog.UpdateProductStatusAsync(wax.Id, new ProductStatusUpdateRequest(true)));

    // Once its product is (logically) deleted, the category can go too, and both names are free again.
    await _catalog.DeleteProductAsync(wax.Id);
    await Assert.ThrowsAsync<KeyNotFoundException>(() => _catalog.GetProductAsync(wax.Id));
    await _catalog.DeleteCategoryAsync(waxes.Id);
    Assert.Empty(await _catalog.GetCategoriesAsync());
    var again = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("Ceras", 1));
    Assert.NotEqual(waxes.PublicCode, again.PublicCode);
    Assert.Equal("Cera mate", (await _catalog.CreateProductAsync(new ProductCreateRequest(again.Id, "Cera mate", 35000))).Name);
  }

  [Fact]
  public async Task Lists_FollowTheCategoryOrderThenTheProductOrder_AndFilterByCategory()
  {
    var oils = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("Aceites", 5));
    var waxes = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("Ceras", 1));
    await _catalog.CreateProductAsync(new ProductCreateRequest(oils.Id, "Aceite", 20000));
    await _catalog.CreateProductAsync(new ProductCreateRequest(waxes.Id, "Pomada", 30000, SortOrder: 2));
    await _catalog.CreateProductAsync(new ProductCreateRequest(waxes.Id, "Cera", 30000, SortOrder: 2));
    await _catalog.CreateProductAsync(new ProductCreateRequest(waxes.Id, "Gel", 30000, SortOrder: 1));

    Assert.Equal(["Ceras", "Aceites"], (await _catalog.GetCategoriesAsync()).Select(category => category.Name));
    Assert.Equal(["Gel", "Cera", "Pomada", "Aceite"], (await _catalog.GetProductsAsync(new ProductListQuery())).Items.Select(product => product.Name));
    Assert.Equal(["Aceite"], (await _catalog.GetProductsAsync(new ProductListQuery(CategoryId: oils.Id))).Items.Select(product => product.Name));
  }

  // ── Image ─────────────────────────────────────────────────────────────────

  [Fact]
  public async Task TheImage_IsUploadedAsAProductImage_ReplacedAndRemoved_ArchivingTheOldFile()
  {
    var waxes = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("Ceras", 1));
    var wax = await _catalog.CreateProductAsync(new ProductCreateRequest(waxes.Id, "Cera mate", 35000));

    var withImage = await _catalog.UploadProductImageAsync(wax.Id, _adminId, Image("cera.png"));
    Assert.NotNull(withImage.ImageMediaAssetId);
    Assert.StartsWith("https://fake.example.com/", withImage.ImageUrl);
    var first = await _dbContext.MediaAssets.AsNoTracking().SingleAsync(asset => asset.Id == withImage.ImageMediaAssetId);
    Assert.Equal((MediaAssetPurpose.ProductImage, MediaAssetStatus.Ready), (first.Purpose, first.Status));

    var replaced = await _catalog.UploadProductImageAsync(wax.Id, _adminId, Image("cera-2.png"));
    Assert.NotEqual(first.Id, replaced.ImageMediaAssetId);
    Assert.Equal(MediaAssetStatus.Archived, (await _dbContext.MediaAssets.AsNoTracking().SingleAsync(asset => asset.Id == first.Id)).Status);
    Assert.Contains(first.StorageKey, _storage.Deleted);

    var removed = await _catalog.RemoveProductImageAsync(wax.Id);
    Assert.Equal((null, null), (removed.ImageMediaAssetId, removed.ImageUrl));
    Assert.Equal(MediaAssetStatus.Archived,
        (await _dbContext.MediaAssets.AsNoTracking().SingleAsync(asset => asset.Id == replaced.ImageMediaAssetId)).Status);

    // Removing again is a no-op.
    Assert.Null((await _catalog.RemoveProductImageAsync(wax.Id)).ImageMediaAssetId);
  }

  [Fact]
  public async Task AnImageForAnUnknownProduct_IsRefusedBeforeStoringAnything()
  {
    await Assert.ThrowsAsync<KeyNotFoundException>(() => _catalog.UploadProductImageAsync(Guid.NewGuid(), _adminId, Image("x.png")));

    Assert.Empty(await _dbContext.MediaAssets.ToListAsync());
  }

  // ── Public catalog (B6) ───────────────────────────────────────────────────

  [Fact]
  public async Task ThePublicCatalog_ListsOnlyActiveCategoriesWithActiveProducts()
  {
    var waxes = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("Ceras", 2));
    var oils = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("Aceites", 1));
    var empty = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("Vacía", 0));
    var onlyInactive = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("Pausados", 0));
    var paused = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("Apagada", 0));
    await _catalog.CreateProductAsync(new ProductCreateRequest(waxes.Id, "Cera mate", 35000));
    await _catalog.CreateProductAsync(new ProductCreateRequest(waxes.Id, "Pomada", 30000));
    await _catalog.CreateProductAsync(new ProductCreateRequest(oils.Id, "Aceite", 20000));
    var hidden = await _catalog.CreateProductAsync(new ProductCreateRequest(onlyInactive.Id, "Oculto", 1000));
    await _catalog.UpdateProductStatusAsync(hidden.Id, new ProductStatusUpdateRequest(false));
    var pausedProduct = await _catalog.CreateProductAsync(new ProductCreateRequest(paused.Id, "Apagado", 1000));
    await _catalog.UpdateProductStatusAsync(pausedProduct.Id, new ProductStatusUpdateRequest(false));
    await _catalog.UpdateCategoryStatusAsync(paused.Id, new ProductCategoryStatusUpdateRequest(false));

    var categories = await _public.GetCategoriesAsync();

    Assert.Equal(
        [new PublicProductCategoryResponse(oils.PublicCode, "Aceites", 1), new PublicProductCategoryResponse(waxes.PublicCode, "Ceras", 2)],
        categories);
    Assert.NotNull(await _public.GetCategoryAsync(empty.PublicCode)); // reachable by its link, just empty
    Assert.Null(await _public.GetCategoryAsync(paused.PublicCode));
  }

  [Fact]
  public async Task APublicCategory_ShowsItsActiveProductsWithPriceAndImage_ByCode()
  {
    var waxes = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("Ceras", 1));
    var wax = await _catalog.CreateProductAsync(new ProductCreateRequest(waxes.Id, "Cera mate", 35000, "Acabado natural", 2));
    var gel = await _catalog.CreateProductAsync(new ProductCreateRequest(waxes.Id, "Gel", 18000, SortOrder: 1));
    var off = await _catalog.CreateProductAsync(new ProductCreateRequest(waxes.Id, "Retirado", 1000));
    await _catalog.UpdateProductStatusAsync(off.Id, new ProductStatusUpdateRequest(false));
    var gone = await _catalog.CreateProductAsync(new ProductCreateRequest(waxes.Id, "Eliminado", 1000));
    await _catalog.DeleteProductAsync(gone.Id);
    var withImage = await _catalog.UploadProductImageAsync(wax.Id, _adminId, Image("cera.png"));

    var category = (await _public.GetCategoryAsync(waxes.PublicCode.ToUpperInvariant()))!;

    Assert.Equal((waxes.PublicCode, "Ceras"), (category.Code, category.Name));
    Assert.Equal(
        [
          new PublicProductSummaryResponse(gel.PublicCode, "Gel", null, 18000, null),
          new PublicProductSummaryResponse(wax.PublicCode, "Cera mate", "Acabado natural", 35000, withImage.ImageUrl),
        ],
        category.Products);
  }

  [Fact]
  public async Task APublicProduct_IsFoundByItsCode_OnlyWhileItAndItsCategoryAreActive()
  {
    var waxes = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("Ceras", 1));
    var wax = await _catalog.CreateProductAsync(new ProductCreateRequest(waxes.Id, "Cera mate", 35000, "Acabado natural"));

    Assert.Equal(
        new PublicProductResponse(wax.PublicCode, "Cera mate", "Acabado natural", 35000, null, waxes.PublicCode, "Ceras"),
        await _public.GetProductAsync(wax.PublicCode));

    // A rename keeps the link working.
    await _catalog.UpdateProductAsync(wax.Id, new ProductUpdateRequest(waxes.Id, "Cera mate premium", 38000));
    Assert.Equal("Cera mate premium", (await _public.GetProductAsync(wax.PublicCode))!.Name);

    await _catalog.UpdateProductStatusAsync(wax.Id, new ProductStatusUpdateRequest(false));
    Assert.Null(await _public.GetProductAsync(wax.PublicCode));
    await _catalog.UpdateCategoryStatusAsync(waxes.Id, new ProductCategoryStatusUpdateRequest(false));
    Assert.Null(await _public.GetCategoryAsync(waxes.PublicCode));

    Assert.Null(await _public.GetProductAsync("zzzzzzzz"));
    Assert.Null(await _public.GetProductAsync("../etc"));
    Assert.Null(await _public.GetCategoryAsync(string.Empty));
  }

  [Fact]
  public async Task APublicProduct_OfAnInactiveCategory_IsNotAvailable()
  {
    var waxes = await _catalog.CreateCategoryAsync(new ProductCategoryCreateRequest("Ceras", 1));
    var wax = await _catalog.CreateProductAsync(new ProductCreateRequest(waxes.Id, "Cera mate", 35000));
    // Bypass the master rules to reach a state they forbid (an active product in an inactive category).
    var category = await _dbContext.ProductCategories.SingleAsync(candidate => candidate.Id == waxes.Id);
    category.Deactivate(DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();

    Assert.Null(await _public.GetProductAsync(wax.PublicCode));
  }

  private static ProductImageUploadRequest Image(string fileName)
      => new(fileName, "image/png", 4, new MemoryStream([0x89, 0x50, 0x4E, 0x47]));

  private sealed class FakeMediaAssetsService(AppDbContext dbContext) : IMediaAssetsService
  {
    public async Task<MediaAssetView> UploadAsync(
        Guid currentUserId, IReadOnlyCollection<string> roles, MediaAssetUploadRequest request, CancellationToken cancellationToken = default)
    {
      Assert.Contains(RoleNames.Admin, roles);
      var now = DateTime.UtcNow;
      var asset = new MediaAsset(request.FileName, request.ContentType, request.SizeBytes, $"fake/{Guid.NewGuid():N}", request.Purpose, currentUserId, now);
      asset.MarkReady($"https://fake.example.com/{asset.StorageKey}", now);
      dbContext.MediaAssets.Add(asset);
      await dbContext.SaveChangesAsync(cancellationToken);
      return new MediaAssetView(
          asset.Id, asset.FileName, asset.ContentType, asset.SizeBytes, asset.StorageKey, asset.PublicUrl, asset.Purpose,
          asset.Status, asset.UploadedByUserId, asset.FailureReason, asset.CreatedAt, asset.UpdatedAt);
    }

    public Task<IReadOnlyList<MediaAssetView>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<MediaAssetView> GetByIdAsync(Guid mediaAssetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task DeleteAsync(Guid mediaAssetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  private sealed class FakeFileStorageService : IFileStorageService
  {
    public List<string> Deleted { get; } = [];

    public Task<StoredFileResult> UploadAsync(FileStorageObject file, CancellationToken cancellationToken = default)
        => Task.FromResult(new StoredFileResult(file.ObjectKey, null));

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
      Deleted.Add(objectKey);
      return Task.CompletedTask;
    }

    public string? GetPublicUrl(string storageKey) => null;
  }
}
