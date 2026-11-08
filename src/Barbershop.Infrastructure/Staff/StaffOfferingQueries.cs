using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Staff;

/// <summary>
/// The offerings a customer can book right now (ADR-0016 B2/B3): active offering, of a live and active general service
/// whose category is live and active. Shared by the public profile, availability, booking and the manual forms so
/// they all agree on what is bookable.
/// </summary>
internal static class StaffOfferingQueries
{
    public sealed record BookableOffering(
        Guid StaffProfileId,
        Guid GeneralServiceId,
        string Name,
        string? Description,
        Guid CategoryId,
        string CategoryName,
        int CategorySortOrder,
        int SortOrder,
        int DurationMinutes);

    /// <summary>Filters on the entities first and projects last, so the whole query translates to SQL.</summary>
    private static IQueryable<BookableOffering> Bookable(
        this AppDbContext db, IReadOnlyCollection<Guid> staffProfileIds, Guid? generalServiceId)
        => from offering in db.StaffServiceOfferings.AsNoTracking()
           join service in db.GeneralServices.AsNoTracking() on offering.GeneralServiceId equals service.Id
           join category in db.ServiceCategories.AsNoTracking() on service.CategoryId equals category.Id
           where staffProfileIds.Contains(offering.StaffProfileId)
               && (generalServiceId == null || offering.GeneralServiceId == generalServiceId)
               && offering.IsActive
               && service.IsActive && !service.IsDeleted
               && category.IsActive && !category.IsDeleted
           select new BookableOffering(
               offering.StaffProfileId,
               service.Id,
               service.Name,
               service.Description,
               category.Id,
               category.Name,
               category.SortOrder,
               service.SortOrder,
               offering.DurationMinutes);

    public static async Task<IReadOnlyList<BookableOffering>> GetBookableAsync(
        this AppDbContext db, IReadOnlyCollection<Guid> staffProfileIds, CancellationToken cancellationToken)
    {
        if (staffProfileIds.Count == 0)
        {
            return [];
        }

        var offerings = await db.Bookable(staffProfileIds, null).ToListAsync(cancellationToken);
        return offerings
            .OrderBy(offering => offering.CategorySortOrder)
            .ThenBy(offering => offering.CategoryName)
            .ThenBy(offering => offering.SortOrder)
            .ThenBy(offering => offering.Name)
            .ToArray();
    }

    public static Task<BookableOffering?> FindBookableAsync(
        this AppDbContext db, Guid staffProfileId, Guid generalServiceId, CancellationToken cancellationToken)
        => db.Bookable([staffProfileId], generalServiceId).SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Exclusive lock on the professional's row, held until the transaction ends. Every change to their agenda
    /// (booking, rescheduling, manual appointments) and to their offerings takes it first, so two of them for the
    /// same professional run one after the other and the second one revalidates against what the first committed.
    /// No-op outside PostgreSQL.
    /// </summary>
    public static async Task LockStaffProfileAsync(this AppDbContext db, Guid staffProfileId, CancellationToken cancellationToken)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM public.staff_profiles WHERE \"Id\" = {staffProfileId} FOR UPDATE", cancellationToken);
        }
    }
}
