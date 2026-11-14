using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Staff.Admin;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Staff;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Barbershop.Tests.Features.Staff;

/// <summary>ADR-0016 B2: what each professional offers and how long it takes them.</summary>
public sealed class StaffOfferingManagementServiceTests : IDisposable
{
    private static readonly Guid Corte = ServiceCatalogDefaults.CorteGeneralServiceId;

    private readonly AppDbContext _dbContext;
    private readonly StaffOfferingManagementService _service;
    private readonly StaffProfile _barber;

    public StaffOfferingManagementServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbContext = new AppDbContext(options);
        _dbContext.Database.EnsureCreated(); // seeds Barbería / Corte
        _service = new StaffOfferingManagementService(_dbContext, TimeProvider.System);

        var user = new User("Alex Barbero", "alex@example.com", "hash", DateTime.UtcNow);
        _barber = new StaffProfile(user.Id, "Alex", 45, DateTime.UtcNow);
        _dbContext.Users.Add(user);
        _dbContext.StaffProfiles.Add(_barber);
        _dbContext.SaveChanges();
    }

    public void Dispose() => _dbContext.Dispose();

    [Fact]
    public async Task Replace_AddsUpdatesAndRemoves_LeavingExactlyWhatWasSent()
    {
        var beard = await ServiceOfferingTestData.AddGeneralServiceAsync(_dbContext, "Barba");
        var color = await ServiceOfferingTestData.AddGeneralServiceAsync(_dbContext, "Color");

        var first = await _service.ReplaceAsync(_barber.Id, Request((Corte, 30, true), (beard.Id, 20, true)));
        Assert.Equal(45, first.DefaultAppointmentDurationMinutes);
        Assert.Equal(
            [(Corte, "Corte", "Barbería", 30, true), (beard.Id, "Barba", "Barbería", 20, true)],
            first.Offerings.Select(offering => (offering.GeneralServiceId, offering.GeneralServiceName, offering.CategoryName, offering.DurationMinutes, offering.IsActive)));
        var corteRowId = (await _dbContext.StaffServiceOfferings.SingleAsync(item => item.GeneralServiceId == Corte)).Id;

        var second = await _service.ReplaceAsync(_barber.Id, Request((Corte, 40, false), (color.Id, 90, true)));

        Assert.Equal(
            [(Corte, 40, false), (color.Id, 90, true)],
            second.Offerings.Select(offering => (offering.GeneralServiceId, offering.DurationMinutes, offering.IsActive)));
        // The existing row is updated in place (unique per professional and general service); Barba is removed.
        Assert.Equal(corteRowId, (await _dbContext.StaffServiceOfferings.SingleAsync(item => item.GeneralServiceId == Corte)).Id);
        Assert.Equal(2, await _dbContext.StaffServiceOfferings.CountAsync());

        var empty = await _service.ReplaceAsync(_barber.Id, Request());
        Assert.Empty(empty.Offerings);
        Assert.Empty((await _service.GetAsync(_barber.Id)).Offerings);
    }

    /// <summary>
    /// BUG-079: an offering left behind by a general service deleted afterwards (data from before the fix) is not shown
    /// and no longer blocks saving: it is dropped instead of answering 422 «El servicio general no existe».
    /// </summary>
    [Fact]
    public async Task Replace_DropsAnOfferingWhoseGeneralServiceWasDeleted_InsteadOfBlockingTheSave()
    {
        var beard = await ServiceOfferingTestData.AddGeneralServiceAsync(_dbContext, "Barba");
        await _service.ReplaceAsync(_barber.Id, Request((Corte, 30, true), (beard.Id, 20, true)));
        beard.MarkDeleted(DateTime.UtcNow);
        await _dbContext.SaveChangesAsync();

        var shown = await _service.GetAsync(_barber.Id);
        Assert.Equal([Corte], shown.Offerings.Select(offering => offering.GeneralServiceId));

        // The admin's screen still had the orphan in its list and sends it back with a change to Corte.
        var saved = await _service.ReplaceAsync(_barber.Id, Request((Corte, 45, true), (beard.Id, 20, true)));

        Assert.Equal([(Corte, 45)], saved.Offerings.Select(offering => (offering.GeneralServiceId, offering.DurationMinutes)));
        Assert.False(await _dbContext.StaffServiceOfferings.AnyAsync(offering => offering.GeneralServiceId == beard.Id));
    }

    [Fact]
    public async Task Replace_ValidatesDurationsDuplicatesAndGeneralServices()
    {
        var deleted = await ServiceOfferingTestData.AddGeneralServiceAsync(_dbContext, "Eliminado");
        deleted.MarkDeleted(DateTime.UtcNow);
        await _dbContext.SaveChangesAsync();

        var shape = await Assert.ThrowsAsync<ValidationProblemException>(() => _service.ReplaceAsync(
            _barber.Id, Request((Corte, 9, true), (Corte, 30, true), (Guid.Empty, 30, true), (Guid.NewGuid(), 241, true))));
        Assert.Contains("offerings[0].durationMinutes", shape.Errors.Keys);
        Assert.Contains("offerings[1].generalServiceId", shape.Errors.Keys);
        Assert.Contains("offerings[2].generalServiceId", shape.Errors.Keys);
        Assert.Contains("offerings[3].durationMinutes", shape.Errors.Keys);

        var unknown = await Assert.ThrowsAsync<ValidationProblemException>(() => _service.ReplaceAsync(
            _barber.Id, Request((Guid.NewGuid(), 30, true), (deleted.Id, 30, false))));
        Assert.Equal(["offerings[0].generalServiceId", "offerings[1].generalServiceId"], unknown.Errors.Keys.Order());

        var missingList = await Assert.ThrowsAsync<ValidationProblemException>(() => _service.ReplaceAsync(
            _barber.Id, new StaffServiceOfferingsReplaceRequest(null)));
        Assert.Contains("offerings", missingList.Errors.Keys);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.ReplaceAsync(Guid.NewGuid(), Request((Corte, 30, true))));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.GetAsync(Guid.NewGuid()));
        Assert.Empty(await _dbContext.StaffServiceOfferings.ToListAsync());
    }

    [Fact]
    public async Task AddingOrActivating_NeedsAnActiveGeneralService_ButKeepingAnExistingOneDoesNot()
    {
        var beard = await ServiceOfferingTestData.AddGeneralServiceAsync(_dbContext, "Barba");
        await _service.ReplaceAsync(_barber.Id, Request((Corte, 30, true), (beard.Id, 20, false)));

        beard.Deactivate(DateTime.UtcNow);
        var corte = await _dbContext.GeneralServices.SingleAsync(service => service.Id == Corte);
        corte.Deactivate(DateTime.UtcNow);
        await _dbContext.SaveChangesAsync();

        // Activating Barba (inactive general service) is a conflict and nothing changes.
        var conflict = await Assert.ThrowsAsync<ConflictException>(() =>
            _service.ReplaceAsync(_barber.Id, Request((Corte, 30, true), (beard.Id, 20, true))));
        Assert.Contains("Barba", conflict.Message);

        // Corte was already active: its duration can still change, and it can be deactivated, without a conflict.
        var kept = await _service.ReplaceAsync(_barber.Id, Request((Corte, 35, true), (beard.Id, 25, false)));
        var corteView = Assert.Single(kept.Offerings, offering => offering.GeneralServiceId == Corte);
        Assert.Equal((35, true, false), (corteView.DurationMinutes, corteView.IsActive, corteView.GeneralServiceIsActive));

        // A new active offering of an inactive general service is a conflict too; sent inactive, it is accepted.
        var color = await ServiceOfferingTestData.AddGeneralServiceAsync(_dbContext, "Color");
        color.Deactivate(DateTime.UtcNow);
        await _dbContext.SaveChangesAsync();
        await Assert.ThrowsAsync<ConflictException>(() =>
            _service.ReplaceAsync(_barber.Id, Request((Corte, 35, true), (color.Id, 60, true))));
        var inactiveNew = await _service.ReplaceAsync(_barber.Id, Request((Corte, 35, true), (color.Id, 60, false)));
        Assert.Contains(inactiveNew.Offerings, offering => offering.GeneralServiceId == color.Id && !offering.IsActive);
    }

    [Fact]
    public void TheOffering_KeepsItsDurationBetween10And240Minutes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StaffServiceOffering(_barber.Id, Corte, 9, true, DateTime.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StaffServiceOffering(_barber.Id, Corte, 241, true, DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() => new StaffServiceOffering(_barber.Id, Guid.Empty, 30, true, DateTime.UtcNow));
        var offering = new StaffServiceOffering(_barber.Id, Corte, 10, true, DateTime.UtcNow);
        Assert.False(offering.Update(10, true, DateTime.UtcNow));
        Assert.Null(offering.UpdatedAt);
        Assert.True(offering.Update(240, false, DateTime.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => offering.Update(0, true, DateTime.UtcNow));
    }

    private static StaffServiceOfferingsReplaceRequest Request(params (Guid GeneralServiceId, int Minutes, bool IsActive)[] items)
        => new(items.Select(item => new StaffServiceOfferingRequest(item.GeneralServiceId, item.Minutes, item.IsActive)).ToArray());
}
