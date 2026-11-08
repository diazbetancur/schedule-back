using Barbershop.Application.Common.Exceptions;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Barbershop.Infrastructure.Products;

/// <summary>
/// Row locks of the product catalog (ADR-0016 B5), the same scheme as the service catalog (B1), always taken in this
/// order inside one transaction: product category → product. A change that adds, moves or revives a product takes a
/// shared lock on its category and then re-reads it; deactivating or deleting the category takes an exclusive lock and
/// then counts its products. Whichever goes second waits for the first to commit and revalidates against what it
/// committed, so an inactive or deleted category never ends up with live products. No-ops outside PostgreSQL.
/// </summary>
internal static class ProductCatalogLocks
{
    public const string CategoryNameTakenMessage = "Ya existe una categoría de productos con ese nombre.";
    public const string ProductNameTakenMessage = "Ya existe un producto con ese nombre en esta categoría.";

    public static Task ShareProductCategoryAsync(this AppDbContext db, Guid categoryId, CancellationToken ct)
        => LockAsync(db, "SELECT 1 FROM public.product_categories WHERE \"Id\" = {0} FOR SHARE", categoryId, ct);

    public static Task LockProductCategoryAsync(this AppDbContext db, Guid categoryId, CancellationToken ct)
        => LockAsync(db, "SELECT 1 FROM public.product_categories WHERE \"Id\" = {0} FOR UPDATE", categoryId, ct);

    public static Task LockProductAsync(this AppDbContext db, Guid productId, CancellationToken ct)
        => LockAsync(db, "SELECT 1 FROM public.products WHERE \"Id\" = {0} FOR UPDATE", productId, ct);

    /// <summary>
    /// Saves and commits, turning a duplicated name rejected by its unique index into a 409 (the pre-check of the
    /// service cannot see a duplicate that a simultaneous request has not committed yet).
    /// </summary>
    public static async Task SaveProductCatalogAndCommitAsync(this AppDbContext db, IDbContextTransaction transaction, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: ProductCatalogConstraints.CategoryNameUnique or ProductCatalogConstraints.ProductNameUnique,
        } unique)
        {
            db.ChangeTracker.Clear();
            throw new ConflictException(unique.ConstraintName == ProductCatalogConstraints.CategoryNameUnique
                ? CategoryNameTakenMessage
                : ProductNameTakenMessage);
        }
    }

    private static async Task LockAsync(AppDbContext db, string sql, Guid id, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(sql, [id], ct);
        }
    }
}
