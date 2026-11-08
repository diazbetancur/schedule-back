using Barbershop.Application.Common.Exceptions;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Barbershop.Infrastructure.Services;

/// <summary>
/// Row locks of the service catalog (ADR-0016 B1), always taken in this order inside one transaction:
/// service category → general service → rate. A change that adds or revives a child takes a shared lock on its
/// parent and then re-reads the parent; deactivating or deleting the parent takes an exclusive lock and then counts
/// its children. Whichever goes second waits for the first to commit and revalidates against what it committed, so
/// an inactive or deleted parent never ends up with live children. No-ops outside PostgreSQL.
/// </summary>
internal static class ServiceCatalogLocks
{
    public const string CategoryNameTakenMessage = "Ya existe una categoría de servicios con ese nombre.";
    public const string GeneralServiceNameTakenMessage = "Ya existe un servicio general con ese nombre en esta categoría.";

    public static Task ShareCategoryAsync(this AppDbContext db, Guid categoryId, CancellationToken ct)
        => LockAsync(db, "SELECT 1 FROM public.service_categories WHERE \"Id\" = {0} FOR SHARE", categoryId, ct);

    public static Task LockCategoryAsync(this AppDbContext db, Guid categoryId, CancellationToken ct)
        => LockAsync(db, "SELECT 1 FROM public.service_categories WHERE \"Id\" = {0} FOR UPDATE", categoryId, ct);

    public static Task ShareGeneralServiceAsync(this AppDbContext db, Guid generalServiceId, CancellationToken ct)
        => LockAsync(db, "SELECT 1 FROM public.general_services WHERE \"Id\" = {0} FOR SHARE", generalServiceId, ct);

    public static Task LockGeneralServiceAsync(this AppDbContext db, Guid generalServiceId, CancellationToken ct)
        => LockAsync(db, "SELECT 1 FROM public.general_services WHERE \"Id\" = {0} FOR UPDATE", generalServiceId, ct);

    public static Task LockRateAsync(this AppDbContext db, Guid rateId, CancellationToken ct)
        => LockAsync(db, "SELECT 1 FROM public.services WHERE \"Id\" = {0} FOR UPDATE", rateId, ct);

    /// <summary>
    /// Saves and commits, turning a duplicated catalog name rejected by its unique index into a 409 (the
    /// pre-check of the service cannot see a duplicate that a simultaneous request has not committed yet).
    /// </summary>
    public static async Task SaveAndCommitAsync(
        this AppDbContext db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: ServiceCatalogConstraints.CategoryNameUnique or ServiceCatalogConstraints.GeneralServiceNameUnique,
        } unique)
        {
            db.ChangeTracker.Clear();
            throw new ConflictException(unique.ConstraintName == ServiceCatalogConstraints.CategoryNameUnique
                ? CategoryNameTakenMessage
                : GeneralServiceNameTakenMessage);
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
