using Barbershop.Application.Storage;
using Barbershop.Domain.Media;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Media;

/// <summary>
/// Shared rules for linking a media asset to a profile slot and for deleting the file a slot no longer uses
/// (BUG-019). Every caller locks the asset row (<c>media_assets</c>, <c>FOR UPDATE</c>) before checking, so linking
/// and deleting the same asset run one after the other: a link that wins makes the delete see a reference and keep
/// the file; a delete that wins archives the asset and the link is rejected.
/// </summary>
internal static class MediaAssetReferences
{
    /// <summary>Locks the asset rows until the surrounding transaction ends. A no-op outside PostgreSQL.</summary>
    public static async Task LockAsync(AppDbContext dbContext, IReadOnlyCollection<Guid> mediaAssetIds, CancellationToken cancellationToken)
    {
        if (mediaAssetIds.Count == 0 || !dbContext.Database.IsNpgsql())
        {
            return;
        }

        var ids = mediaAssetIds.Distinct().OrderBy(id => id).ToArray();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM public.media_assets WHERE \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR UPDATE", cancellationToken);
    }

    /// <summary>
    /// Whether anything in the platform points to the asset: account avatars, staff photo and tips QR, banners,
    /// products (also logically deleted ones) and branding.
    /// </summary>
    /// <param name="exceptUserId">An account whose avatar reference does not count (the slot being linked syncs it).</param>
    /// <param name="exceptStaffProfileId">A staff profile whose own slots do not count.</param>
    public static async Task<bool> IsReferencedAsync(
        AppDbContext dbContext,
        Guid mediaAssetId,
        CancellationToken cancellationToken,
        Guid? exceptUserId = null,
        Guid? exceptStaffProfileId = null)
    {
        if (await dbContext.Users.AsNoTracking()
                .AnyAsync(user => user.ProfilePhotoMediaAssetId == mediaAssetId && user.Id != exceptUserId, cancellationToken))
        {
            return true;
        }

        if (await dbContext.StaffProfiles.AsNoTracking()
                .AnyAsync(staff => (staff.PhotoMediaAssetId == mediaAssetId || staff.TipsQrMediaAssetId == mediaAssetId)
                    && staff.Id != exceptStaffProfileId, cancellationToken))
        {
            return true;
        }

        if (await dbContext.Banners.AsNoTracking().AnyAsync(banner => banner.ImageMediaAssetId == mediaAssetId, cancellationToken))
        {
            return true;
        }

        // BUG-099: a deleted product no longer holds its image (there is no way to restore a product), so the file can be
        // archived; deleting the product archives it.
        if (await dbContext.Products.AsNoTracking().AnyAsync(product => product.ImageMediaAssetId == mediaAssetId && !product.IsDeleted, cancellationToken))
        {
            return true;
        }

        return await dbContext.AppBrandingSettings.AsNoTracking()
            .AnyAsync(branding => branding.LogoMediaAssetId == mediaAssetId || branding.AppIconMediaAssetId == mediaAssetId, cancellationToken);
    }

    /// <summary>Whether the account currently holds the Admin role.</summary>
    public static Task<bool> IsAdminAsync(AppDbContext dbContext, Guid userId, CancellationToken cancellationToken)
    {
        var adminRole = RoleNames.Admin.ToUpperInvariant();
        return dbContext.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .SelectMany(user => user.UserRoles)
            .AnyAsync(userRole => userRole.Role.NormalizedName == adminRole, cancellationToken);
    }

    /// <summary>
    /// Deletes the file of an asset a slot stopped using, but only when <paramref name="mayDelete"/> accepts it and
    /// nothing else references it; otherwise the file is left untouched (the slot was already unlinked by the caller).
    /// The asset is archived in a transaction under its row lock; the storage object is deleted after the commit, and
    /// a storage failure is queued as a pending deletion. Returns whether the asset was archived.
    /// </summary>
    public static async Task<bool> ReleaseAsync(
        AppDbContext dbContext,
        IFileStorageService fileStorageService,
        TimeProvider timeProvider,
        Guid mediaAssetId,
        Func<MediaAsset, Task<bool>> mayDelete,
        string reason,
        CancellationToken cancellationToken)
    {
        var archivedStorageKey = await InTransactionAsync(dbContext, async () =>
        {
            await LockAsync(dbContext, [mediaAssetId], cancellationToken);

            var asset = await dbContext.MediaAssets.SingleOrDefaultAsync(candidate => candidate.Id == mediaAssetId, cancellationToken);
            if (dbContext.Database.IsRelational() && asset is not null)
            {
                await dbContext.Entry(asset).ReloadAsync(cancellationToken);
            }

            if (asset is null
                || asset.Status is not MediaAssetStatus.Ready
                || !await mayDelete(asset)
                || await IsReferencedAsync(dbContext, mediaAssetId, cancellationToken))
            {
                return null;
            }

            asset.Archive(timeProvider.GetUtcNow().UtcDateTime);
            await dbContext.SaveChangesAsync(cancellationToken);
            return asset.StorageKey;
        }, cancellationToken);

        if (archivedStorageKey is null)
        {
            return false;
        }

        try
        {
            await fileStorageService.DeleteAsync(archivedStorageKey, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            dbContext.PendingFileDeletions.Add(
                new PendingFileDeletion(archivedStorageKey, reason, timeProvider.GetUtcNow().UtcDateTime));
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    public static async Task<T> InTransactionAsync<T>(AppDbContext dbContext, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational() || dbContext.Database.CurrentTransaction is not null)
        {
            return await work();
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var result = await work();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
