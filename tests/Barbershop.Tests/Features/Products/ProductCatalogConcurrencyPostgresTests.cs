using System.Data.Common;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Media;
using Barbershop.Application.Products;
using Barbershop.Application.Storage;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Products;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Barbershop.Tests.Features.Products;

/// <summary>
/// Simultaneous product catalog changes on real PostgreSQL (ADR-0016 B5), forced deterministically in both orders as in
/// ServiceCatalogConcurrencyPostgresTests: the first operation is held right after the statement that takes its lock
/// (or writes its row), the second is observed waiting in pg_stat_activity, then the first is released. Whatever the
/// order, a category that ends inactive or deleted has no live product added, and two equivalent names end as one row
/// plus a controlled conflict.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "ProductCatalogPostgres")]
public sealed class ProductCatalogConcurrencyPostgresTests
{
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

  private readonly PostgresContainerFixture _postgres;

  public ProductCatalogConcurrencyPostgresTests(PostgresContainerFixture postgres) => _postgres = postgres;

  // ── Category ↔ product ────────────────────────────────────────────────────

  [Fact]
  public async Task DeactivatingTheCategoryFirst_MakesTheWaitingProductCreationAConflict()
  {
    if (!_postgres.IsAvailable) return; // Only with PostgresContainerFixture.AllowSkipVariable set on purpose.
    var waxes = await SeedCategoryAsync("Ceras", reset: true);

    var (deactivate, create) = await RaceAsync(
        Statement("product_categories", "FOR UPDATE"),
        context => Catalog(context).UpdateCategoryStatusAsync(waxes, new ProductCategoryStatusUpdateRequest(false)),
        context => Catalog(context).CreateProductAsync(new ProductCreateRequest(waxes, "Cera mate", 35000)));

    Assert.Null(deactivate);
    Assert.IsType<ConflictException>(create);
    await using var verify = CreateContext(null);
    Assert.False((await verify.ProductCategories.AsNoTracking().SingleAsync(category => category.Id == waxes)).IsActive);
    Assert.False(await verify.Products.AnyAsync(product => product.CategoryId == waxes));
  }

  [Fact]
  public async Task CreatingAProductFirst_MakesTheWaitingCategoryDeactivationAConflict()
  {
    if (!_postgres.IsAvailable) return;
    var waxes = await SeedCategoryAsync("Ceras", reset: true);

    var (create, deactivate) = await RaceAsync(
        Statement("product_categories", "FOR SHARE"),
        context => Catalog(context).CreateProductAsync(new ProductCreateRequest(waxes, "Cera mate", 35000)),
        context => Catalog(context).UpdateCategoryStatusAsync(waxes, new ProductCategoryStatusUpdateRequest(false)));

    Assert.Null(create);
    Assert.IsType<ConflictException>(deactivate);
    await using var verify = CreateContext(null);
    Assert.True((await verify.ProductCategories.AsNoTracking().SingleAsync(category => category.Id == waxes)).IsActive);
    Assert.True((await verify.Products.AsNoTracking().SingleAsync(product => product.CategoryId == waxes)).IsActive);
  }

  [Fact]
  public async Task DeletingTheCategoryFirst_RefusesTheWaitingMoveIntoIt()
  {
    if (!_postgres.IsAvailable) return;
    var waxes = await SeedCategoryAsync("Ceras", reset: true);
    var oils = await SeedCategoryAsync("Aceites");
    var oil = await SeedProductAsync(oils, "Aceite");

    var (delete, move) = await RaceAsync(
        Statement("product_categories", "FOR UPDATE"),
        context => Catalog(context).DeleteCategoryAsync(waxes),
        context => Catalog(context).UpdateProductAsync(oil, new ProductUpdateRequest(waxes, "Aceite", 20000)));

    Assert.Null(delete);
    Assert.IsType<ValidationProblemException>(move); // the category no longer exists
    await using var verify = CreateContext(null);
    Assert.True((await verify.ProductCategories.AsNoTracking().SingleAsync(category => category.Id == waxes)).IsDeleted);
    Assert.Equal(oils, (await verify.Products.AsNoTracking().SingleAsync(product => product.Id == oil)).CategoryId);
  }

  [Fact]
  public async Task MovingIntoTheCategoryFirst_MakesTheWaitingCategoryDeletionAConflict()
  {
    if (!_postgres.IsAvailable) return;
    var waxes = await SeedCategoryAsync("Ceras", reset: true);
    var oils = await SeedCategoryAsync("Aceites");
    var oil = await SeedProductAsync(oils, "Aceite");

    var (move, delete) = await RaceAsync(
        Statement("products", "FOR UPDATE"),
        context => Catalog(context).UpdateProductAsync(oil, new ProductUpdateRequest(waxes, "Aceite", 20000)),
        context => Catalog(context).DeleteCategoryAsync(waxes));

    Assert.Null(move);
    Assert.IsType<ConflictException>(delete);
    await using var verify = CreateContext(null);
    Assert.False((await verify.ProductCategories.AsNoTracking().SingleAsync(category => category.Id == waxes)).IsDeleted);
    Assert.Equal(waxes, (await verify.Products.AsNoTracking().SingleAsync(product => product.Id == oil)).CategoryId);
  }

  [Fact]
  public async Task DeactivatingTheCategoryFirst_MakesTheWaitingProductReactivationAConflict()
  {
    if (!_postgres.IsAvailable) return;
    var waxes = await SeedCategoryAsync("Ceras", reset: true);
    var wax = await SeedProductAsync(waxes, "Cera mate", isActive: false);

    var (deactivate, reactivate) = await RaceAsync(
        Statement("product_categories", "FOR UPDATE"),
        context => Catalog(context).UpdateCategoryStatusAsync(waxes, new ProductCategoryStatusUpdateRequest(false)),
        context => Catalog(context).UpdateProductStatusAsync(wax, new ProductStatusUpdateRequest(true)));

    Assert.Null(deactivate);
    Assert.IsType<ConflictException>(reactivate);
    await using var verify = CreateContext(null);
    Assert.False((await verify.ProductCategories.AsNoTracking().SingleAsync(category => category.Id == waxes)).IsActive);
    Assert.False((await verify.Products.AsNoTracking().SingleAsync(product => product.Id == wax)).IsActive);
  }

  [Fact]
  public async Task ReactivatingAProductFirst_MakesTheWaitingCategoryDeactivationAConflict()
  {
    if (!_postgres.IsAvailable) return;
    var waxes = await SeedCategoryAsync("Ceras", reset: true);
    var wax = await SeedProductAsync(waxes, "Cera mate", isActive: false);

    var (reactivate, deactivate) = await RaceAsync(
        Statement("product_categories", "FOR SHARE"),
        context => Catalog(context).UpdateProductStatusAsync(wax, new ProductStatusUpdateRequest(true)),
        context => Catalog(context).UpdateCategoryStatusAsync(waxes, new ProductCategoryStatusUpdateRequest(false)));

    Assert.Null(reactivate);
    Assert.IsType<ConflictException>(deactivate);
    await using var verify = CreateContext(null);
    Assert.True((await verify.ProductCategories.AsNoTracking().SingleAsync(category => category.Id == waxes)).IsActive);
    Assert.True((await verify.Products.AsNoTracking().SingleAsync(product => product.Id == wax)).IsActive);
  }

  // ── Equivalent names ──────────────────────────────────────────────────────

  [Fact]
  public async Task TwoEquivalentProductNames_InTheSameCategory_EndAsOneRowAndAConflict()
  {
    if (!_postgres.IsAvailable) return;
    var waxes = await SeedCategoryAsync("Ceras", reset: true);

    var (first, second) = await RaceAsync(
        Statement("INSERT INTO public.products"),
        context => Catalog(context).CreateProductAsync(new ProductCreateRequest(waxes, "Cera mate", 35000)),
        context => Catalog(context).CreateProductAsync(new ProductCreateRequest(waxes, "  CERA MATE ", 30000)));

    Assert.Null(first);
    var conflict = Assert.IsType<ConflictException>(second);
    Assert.Equal("Ya existe un producto con ese nombre en esta categoría.", conflict.Message);
    await using var verify = CreateContext(null);
    Assert.Equal(["Cera mate"], await verify.Products.Where(product => product.NormalizedName == "CERA MATE").Select(product => product.Name).ToListAsync());
  }

  [Fact]
  public async Task AMoveAndACreation_ToTheSameNameInACategory_EndAsOneRowAndAConflict()
  {
    if (!_postgres.IsAvailable) return;
    var waxes = await SeedCategoryAsync("Ceras", reset: true);
    var oils = await SeedCategoryAsync("Aceites");
    var oil = await SeedProductAsync(oils, "Aceite");

    var (move, create) = await RaceAsync(
        Statement("UPDATE public.products"),
        context => Catalog(context).UpdateProductAsync(oil, new ProductUpdateRequest(waxes, "Brillo", 20000)),
        context => Catalog(context).CreateProductAsync(new ProductCreateRequest(waxes, "brillo", 15000)));

    Assert.Null(move);
    Assert.IsType<ConflictException>(create);
    await using var verify = CreateContext(null);
    Assert.Equal([oil], await verify.Products.Where(product => product.NormalizedName == "BRILLO").Select(product => product.Id).ToListAsync());
  }

  [Fact]
  public async Task TwoEquivalentCategoryNames_EndAsOneRowAndAConflict()
  {
    if (!_postgres.IsAvailable) return;
    await ResetAsync();

    var (first, second) = await RaceAsync(
        Statement("INSERT INTO public.product_categories"),
        context => Catalog(context).CreateCategoryAsync(new ProductCategoryCreateRequest("Ceras", 1)),
        context => Catalog(context).CreateCategoryAsync(new ProductCategoryCreateRequest(" ceras ", 2)));

    Assert.Null(first);
    var conflict = Assert.IsType<ConflictException>(second);
    Assert.Equal("Ya existe una categoría de productos con ese nombre.", conflict.Message);
    await using var verify = CreateContext(null);
    Assert.Equal(1, await verify.ProductCategories.CountAsync(category => category.NormalizedName == "CERAS"));
  }

  // ── Helpers ───────────────────────────────────────────────────────────────

  private static Func<string, bool> Statement(params string[] fragments)
      => text => fragments.All(fragment => text.Contains(fragment, StringComparison.Ordinal));

  private async Task ResetAsync()
  {
    await using var context = CreateContext(null);
    await context.Database.EnsureDeletedAsync();
    await context.Database.MigrateAsync();
  }

  private async Task<Guid> SeedCategoryAsync(string name, bool reset = false)
  {
    if (reset)
    {
      await ResetAsync();
    }

    await using var context = CreateContext(null);
    return (await Catalog(context).CreateCategoryAsync(new ProductCategoryCreateRequest(name, 1))).Id;
  }

  private async Task<Guid> SeedProductAsync(Guid categoryId, string name, bool isActive = true)
  {
    await using var context = CreateContext(null);
    var created = await Catalog(context).CreateProductAsync(new ProductCreateRequest(categoryId, name, 20000));
    if (!isActive)
    {
      await Catalog(context).UpdateProductStatusAsync(created.Id, new ProductStatusUpdateRequest(false));
    }

    return created.Id;
  }

  /// <summary>The image pipeline is not exercised here; nothing in these races touches it.</summary>
  private static ProductCatalogManagementService Catalog(AppDbContext context)
      => new(context, TimeProvider.System, new UnusedMediaAssetsService(), new UnusedFileStorageService());

  private async Task<(Exception? Holder, Exception? Contender)> RaceAsync(
      Func<string, bool> pauseAfter, Func<AppDbContext, Task> holder, Func<AppDbContext, Task> contender)
  {
    var gate = new StatementGate(pauseAfter);
    await using var holderContext = CreateContext(gate);
    await using var contenderContext = CreateContext(null);

    var holding = Task.Run(() => Capture(() => holder(holderContext)));
    await gate.Held.WaitAsync(Timeout);
    var contending = Task.Run(() => Capture(() => contender(contenderContext)));
    await WaitUntilASessionWaitsOnALockAsync();
    Assert.False(contending.IsCompleted);

    gate.Release();
    return (await holding.WaitAsync(Timeout), await contending.WaitAsync(Timeout));
  }

  private static async Task<Exception?> Capture(Func<Task> action)
  {
    try
    {
      await action();
      return null;
    }
    catch (Exception exception)
    {
      return exception;
    }
  }

  private async Task WaitUntilASessionWaitsOnALockAsync()
  {
    await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
    await connection.OpenAsync();
    var deadline = DateTime.UtcNow + Timeout;
    while (true)
    {
      await using var command = new NpgsqlCommand(
          "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND datname = current_database()", connection);
      if ((long)(await command.ExecuteScalarAsync())! > 0)
      {
        return;
      }

      Assert.True(DateTime.UtcNow < deadline, "The second operation never waited on a lock.");
      await Task.Delay(50);
    }
  }

  private AppDbContext CreateContext(IInterceptor? interceptor)
  {
    var builder = new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(_postgres.ConnectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName));
    if (interceptor is not null)
    {
      builder.AddInterceptors(interceptor);
    }

    return new AppDbContext(builder.Options);
  }

  /// <summary>Holds the first command matching the predicate, after it ran (its locks taken), until released.</summary>
  private sealed class StatementGate(Func<string, bool> matches) : DbCommandInterceptor
  {
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Held => _held.Task;

    public void Release() => _released.TrySetResult();

    public override async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
      await HoldIfMatchingAsync(command, cancellationToken);
      return result;
    }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
      await HoldIfMatchingAsync(command, cancellationToken);
      return result;
    }

    private async Task HoldIfMatchingAsync(DbCommand command, CancellationToken cancellationToken)
    {
      if (matches(command.CommandText) && _held.TrySetResult())
      {
        await _released.Task.WaitAsync(Timeout, cancellationToken);
      }
    }
  }

  private sealed class UnusedMediaAssetsService : IMediaAssetsService
  {
    public Task<MediaAssetView> UploadAsync(
        Guid currentUserId, IReadOnlyCollection<string> roles, MediaAssetUploadRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<MediaAssetView>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<MediaAssetView> GetByIdAsync(Guid mediaAssetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task DeleteAsync(Guid mediaAssetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  private sealed class UnusedFileStorageService : IFileStorageService
  {
    public Task<StoredFileResult> UploadAsync(FileStorageObject file, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public string? GetPublicUrl(string storageKey) => null;
  }
}
