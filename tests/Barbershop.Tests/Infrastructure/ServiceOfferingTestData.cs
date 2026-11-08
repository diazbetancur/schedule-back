using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Infrastructure;

/// <summary>
/// Offerings for tests (ADR-0016 B2): since B3 a professional can only be booked for a general service they offer.
/// Works on InMemory (where HasData rows only exist after EnsureCreated) and on PostgreSQL (where the migration seeds
/// "Barbería"/"Corte").
/// </summary>
public static class ServiceOfferingTestData
{
    public static Guid Corte => ServiceCatalogDefaults.CorteGeneralServiceId;

    public static async Task EnsureDefaultCatalogAsync(AppDbContext db)
    {
        if (!await db.ServiceCategories.AnyAsync(category => category.Id == ServiceCatalogDefaults.BarberiaCategoryId))
        {
            db.ServiceCategories.Add(new ServiceCategory(
                ServiceCatalogDefaults.BarberiaCategoryName, 0, ServiceCatalogDefaults.SeededAt,
                ServiceCatalogDefaults.BarberiaCategoryId, ServiceCatalogDefaults.BarberiaCategoryCode));
        }

        if (!await db.GeneralServices.AnyAsync(service => service.Id == ServiceCatalogDefaults.CorteGeneralServiceId))
        {
            db.GeneralServices.Add(new GeneralService(
                ServiceCatalogDefaults.BarberiaCategoryId, ServiceCatalogDefaults.CorteGeneralServiceName, null, 0,
                ServiceCatalogDefaults.SeededAt, ServiceCatalogDefaults.CorteGeneralServiceId, ServiceCatalogDefaults.CorteGeneralServiceCode));
        }

        await db.SaveChangesAsync();
    }

    /// <summary>A new active general service in "Barbería".</summary>
    public static async Task<GeneralService> AddGeneralServiceAsync(AppDbContext db, string name)
    {
        await EnsureDefaultCatalogAsync(db);
        var service = new GeneralService(ServiceCatalogDefaults.BarberiaCategoryId, name, null, 1, DateTime.UtcNow);
        db.GeneralServices.Add(service);
        await db.SaveChangesAsync();
        return service;
    }

    public static async Task<StaffServiceOffering> OfferAsync(
        AppDbContext db, Guid staffProfileId, Guid generalServiceId, int durationMinutes, bool isActive = true)
    {
        await EnsureDefaultCatalogAsync(db);
        var offering = new StaffServiceOffering(staffProfileId, generalServiceId, durationMinutes, isActive, DateTime.UtcNow);
        db.StaffServiceOfferings.Add(offering);
        await db.SaveChangesAsync();
        return offering;
    }

    /// <summary>Like <see cref="OfferCorteAsync"/>, but does nothing when the professional already offers it.</summary>
    public static async Task EnsureCorteOfferedAsync(AppDbContext db, Guid staffProfileId)
    {
        if (!await db.StaffServiceOfferings.AnyAsync(offering => offering.StaffProfileId == staffProfileId && offering.GeneralServiceId == Corte))
        {
            await OfferCorteAsync(db, staffProfileId);
        }
    }

    /// <summary>"Corte" at the professional's default duration, as the AddStaffServiceOfferings migration does (F2).</summary>
    public static async Task OfferCorteAsync(AppDbContext db, Guid staffProfileId)
    {
        var duration = await db.StaffProfiles.Where(profile => profile.Id == staffProfileId)
            .Select(profile => profile.DefaultAppointmentDurationMinutes)
            .SingleAsync();
        await OfferAsync(db, staffProfileId, Corte, duration);
    }
}
