using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Services.Admin;
using Barbershop.Domain.Services;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Barbershop.Tests.Features.Services;

/// <summary>ADR-0016 B1: service categories, general services and the rates attached to them.</summary>
public sealed class ServiceCatalogManagementServiceTests : IDisposable
{
  private static readonly Guid Barberia = ServiceCatalogDefaults.BarberiaCategoryId;
  private static readonly Guid Corte = ServiceCatalogDefaults.CorteGeneralServiceId;

  private readonly AppDbContext _dbContext;
  private readonly IAdminServiceCatalogService _catalog;
  private readonly IAdminServicesService _rates;

  public ServiceCatalogManagementServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options;
    _dbContext = new AppDbContext(options);
    _dbContext.Database.EnsureCreated(); // seeds Barbería and Corte
    _catalog = new ServiceCatalogManagementService(_dbContext, TimeProvider.System);
    _rates = new ServiceManagementService(_dbContext, TimeProvider.System, new NullCurrentUserAccessor());
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task TheDefaults_AreBarberiaAndCorte()
  {
    var category = Assert.Single(await _catalog.GetCategoriesAsync());
    Assert.Equal((Barberia, "Barbería", "barberia", true, 1, 1),
        (category.Id, category.Name, category.PublicCode, category.IsActive, category.GeneralServiceCount, category.ActiveGeneralServiceCount));

    var general = Assert.Single(await _catalog.GetGeneralServicesAsync(null));
    Assert.Equal((Corte, Barberia, "Barbería", "Corte", "corte", true),
        (general.Id, general.CategoryId, general.CategoryName, general.Name, general.PublicCode, general.IsActive));
  }

  [Fact]
  public async Task Categories_HaveUniqueNames_IgnoringCaseAndSpaces()
  {
    await _catalog.CreateCategoryAsync(new ServiceCategoryCreateRequest("Cuidado facial", 1));

    await Assert.ThrowsAsync<ConflictException>(() => _catalog.CreateCategoryAsync(new ServiceCategoryCreateRequest("  cuidado FACIAL ", 2)));
    var other = await _catalog.CreateCategoryAsync(new ServiceCategoryCreateRequest("Color", 2));
    await Assert.ThrowsAsync<ConflictException>(() => _catalog.UpdateCategoryAsync(other.Id, new ServiceCategoryUpdateRequest("barbería", 2)));
  }

  [Fact]
  public async Task Categories_AreValidated()
  {
    var error = await Assert.ThrowsAsync<ValidationProblemException>(() => _catalog.CreateCategoryAsync(new ServiceCategoryCreateRequest(" x ", -1)));

    Assert.Contains("name", error.Errors.Keys);
    Assert.Contains("sortOrder", error.Errors.Keys);
  }

  [Fact]
  public async Task Categories_ListBySortOrderThenName()
  {
    await _catalog.CreateCategoryAsync(new ServiceCategoryCreateRequest("Uñas", 5));
    await _catalog.CreateCategoryAsync(new ServiceCategoryCreateRequest("Color", 5));
    await _catalog.CreateCategoryAsync(new ServiceCategoryCreateRequest("Spa", 1));

    var names = (await _catalog.GetCategoriesAsync()).Select(category => category.Name);

    Assert.Equal(["Barbería", "Spa", "Color", "Uñas"], names);
  }

  [Fact]
  public async Task ACategoryWithActiveGeneralServices_CannotBeDeactivated_NorOneWithGeneralServicesDeleted()
  {
    await Assert.ThrowsAsync<ConflictException>(() =>
        _catalog.UpdateCategoryStatusAsync(Barberia, new ServiceCategoryStatusUpdateRequest(false)));
    await Assert.ThrowsAsync<ConflictException>(() => _catalog.DeleteCategoryAsync(Barberia));

    var empty = await _catalog.CreateCategoryAsync(new ServiceCategoryCreateRequest("Temporal", 9));
    Assert.False((await _catalog.UpdateCategoryStatusAsync(empty.Id, new ServiceCategoryStatusUpdateRequest(false))).IsActive);
    await _catalog.DeleteCategoryAsync(empty.Id);

    Assert.DoesNotContain(await _catalog.GetCategoriesAsync(), category => category.Id == empty.Id);
    await Assert.ThrowsAsync<KeyNotFoundException>(() => _catalog.GetCategoryAsync(empty.Id));
    // The name is free again once deleted.
    await _catalog.CreateCategoryAsync(new ServiceCategoryCreateRequest("Temporal", 9));
  }

  [Fact]
  public async Task GeneralServices_NeedAnExistingActiveCategory()
  {
    var missing = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _catalog.CreateGeneralServiceAsync(new GeneralServiceCreateRequest(Guid.NewGuid(), "Barba")));
    Assert.Contains("categoryId", missing.Errors.Keys);

    var empty = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _catalog.CreateGeneralServiceAsync(new GeneralServiceCreateRequest(Guid.Empty, "Barba")));
    Assert.Contains("categoryId", empty.Errors.Keys);

    var inactive = await _catalog.CreateCategoryAsync(new ServiceCategoryCreateRequest("Pausada", 3));
    await _catalog.UpdateCategoryStatusAsync(inactive.Id, new ServiceCategoryStatusUpdateRequest(false));
    await Assert.ThrowsAsync<ConflictException>(() =>
        _catalog.CreateGeneralServiceAsync(new GeneralServiceCreateRequest(inactive.Id, "Barba")));
  }

  [Fact]
  public async Task AGeneralService_CanMoveToAnotherCategory_KeepingItsIdAndPublicCode()
  {
    var beard = await _catalog.CreateGeneralServiceAsync(new GeneralServiceCreateRequest(Barberia, "Barba", "Perfilado y arreglo", 2));
    var care = await _catalog.CreateCategoryAsync(new ServiceCategoryCreateRequest("Cuidado facial", 1));

    var moved = await _catalog.UpdateGeneralServiceAsync(beard.Id, new GeneralServiceUpdateRequest(care.Id, "Barba completa", "  ", 4));

    Assert.Equal((beard.Id, care.Id, "Cuidado facial", "Barba completa", (string?)null, 4, beard.PublicCode),
        (moved.Id, moved.CategoryId, moved.CategoryName, moved.Name, moved.Description, moved.SortOrder, moved.PublicCode));
    Assert.Matches("^[a-z0-9]{8}$", beard.PublicCode);
    Assert.Equal(1, (await _catalog.GetCategoryAsync(care.Id)).GeneralServiceCount);
  }

  [Fact]
  public async Task GeneralServiceNames_AreUniquePerCategory_IgnoringCaseAndOuterSpaces()
  {
    // Equivalent name in the same category: refused.
    await Assert.ThrowsAsync<ConflictException>(() =>
        _catalog.CreateGeneralServiceAsync(new GeneralServiceCreateRequest(Barberia, " CORTE ")));

    // The same name in another category: allowed, with its own public code.
    var hair = await _catalog.CreateCategoryAsync(new ServiceCategoryCreateRequest("Peluquería", 1));
    var hairCut = await _catalog.CreateGeneralServiceAsync(new GeneralServiceCreateRequest(hair.Id, "Corte"));
    Assert.Equal(("Corte", hair.Id), (hairCut.Name, hairCut.CategoryId));
    Assert.NotEqual(ServiceCatalogDefaults.CorteGeneralServiceCode, hairCut.PublicCode);

    Assert.Equal(["Corte"], (await _catalog.GetGeneralServicesAsync(hair.Id)).Select(service => service.Name));
    Assert.Equal(2, (await _catalog.GetGeneralServicesAsync(null)).Count);

    // Renaming within a category to an equivalent name is refused too.
    var beard = await _catalog.CreateGeneralServiceAsync(new GeneralServiceCreateRequest(Barberia, "Barba"));
    await Assert.ThrowsAsync<ConflictException>(() =>
        _catalog.UpdateGeneralServiceAsync(beard.Id, new GeneralServiceUpdateRequest(Barberia, "corte")));
  }

  [Fact]
  public async Task MovingAGeneralService_IntoACategoryWithAnEquivalentName_IsAConflict()
  {
    var hair = await _catalog.CreateCategoryAsync(new ServiceCategoryCreateRequest("Peluquería", 1));
    var hairCut = await _catalog.CreateGeneralServiceAsync(new GeneralServiceCreateRequest(hair.Id, " corte "));

    var conflict = await Assert.ThrowsAsync<ConflictException>(() =>
        _catalog.UpdateGeneralServiceAsync(hairCut.Id, new GeneralServiceUpdateRequest(Barberia, "Corte")));

    Assert.Equal("Ya existe un servicio general con ese nombre en esta categoría.", conflict.Message);
    Assert.Equal(hair.Id, (await _catalog.GetGeneralServiceAsync(hairCut.Id)).CategoryId);

    // Moving it with a different name works.
    var moved = await _catalog.UpdateGeneralServiceAsync(hairCut.Id, new GeneralServiceUpdateRequest(Barberia, "Corte infantil"));
    Assert.Equal((Barberia, "Corte infantil"), (moved.CategoryId, moved.Name));
  }

  [Fact]
  public async Task Names_AreStoredTrimmed_WithTheirNormalizedKey()
  {
    var category = await _catalog.CreateCategoryAsync(new ServiceCategoryCreateRequest("  Cuidado facial  ", 1));
    var service = await _catalog.CreateGeneralServiceAsync(new GeneralServiceCreateRequest(category.Id, "  Limpieza  "));

    Assert.Equal(("Cuidado facial", "Limpieza"), (category.Name, service.Name));
    var stored = await _dbContext.GeneralServices.AsNoTracking().SingleAsync(candidate => candidate.Id == service.Id);
    Assert.Equal("LIMPIEZA", stored.NormalizedName);
    Assert.Equal("CUIDADO FACIAL", (await _dbContext.ServiceCategories.AsNoTracking().SingleAsync(candidate => candidate.Id == category.Id)).NormalizedName);
  }

  [Fact]
  public async Task AGeneralServiceWithActiveRates_CannotBeDeactivated_NorOneWithRatesDeleted()
  {
    var rate = await _rates.CreateAsync(new ServiceCreateRequest("Corte clásico", 25000, 30, GeneralServiceId: Corte));

    await Assert.ThrowsAsync<ConflictException>(() =>
        _catalog.UpdateGeneralServiceStatusAsync(Corte, new GeneralServiceStatusUpdateRequest(false)));
    await Assert.ThrowsAsync<ConflictException>(() => _catalog.DeleteGeneralServiceAsync(Corte));

    // An inactive rate no longer blocks deactivation, but it still blocks deletion.
    await _rates.UpdateStatusAsync(rate.Id, new ServiceStatusUpdateRequest(false));
    Assert.False((await _catalog.UpdateGeneralServiceStatusAsync(Corte, new GeneralServiceStatusUpdateRequest(false))).IsActive);
    await Assert.ThrowsAsync<ConflictException>(() => _catalog.DeleteGeneralServiceAsync(Corte));

    // A rate of an inactive general service cannot be reactivated until its general service is.
    await Assert.ThrowsAsync<ConflictException>(() => _rates.UpdateStatusAsync(rate.Id, new ServiceStatusUpdateRequest(true)));
    await _catalog.UpdateGeneralServiceStatusAsync(Corte, new GeneralServiceStatusUpdateRequest(true));
    Assert.True((await _rates.UpdateStatusAsync(rate.Id, new ServiceStatusUpdateRequest(true))).IsActive);
  }

  [Fact]
  public async Task AGeneralServiceOfAnInactiveCategory_CannotBeActivated()
  {
    var care = await _catalog.CreateCategoryAsync(new ServiceCategoryCreateRequest("Cuidado facial", 1));
    var cleaning = await _catalog.CreateGeneralServiceAsync(new GeneralServiceCreateRequest(care.Id, "Limpieza facial"));
    await _catalog.UpdateGeneralServiceStatusAsync(cleaning.Id, new GeneralServiceStatusUpdateRequest(false));
    await _catalog.UpdateCategoryStatusAsync(care.Id, new ServiceCategoryStatusUpdateRequest(false));

    await Assert.ThrowsAsync<ConflictException>(() =>
        _catalog.UpdateGeneralServiceStatusAsync(cleaning.Id, new GeneralServiceStatusUpdateRequest(true)));
  }

  [Fact]
  public async Task ADeletedGeneralService_LeavesTheListAndFreesItsName()
  {
    var beard = await _catalog.CreateGeneralServiceAsync(new GeneralServiceCreateRequest(Barberia, "Barba"));

    await _catalog.DeleteGeneralServiceAsync(beard.Id);

    Assert.DoesNotContain(await _catalog.GetGeneralServicesAsync(null), service => service.Id == beard.Id);
    var again = await _catalog.CreateGeneralServiceAsync(new GeneralServiceCreateRequest(Barberia, "Barba"));
    Assert.NotEqual(beard.PublicCode, again.PublicCode);
  }

  [Fact]
  public async Task ARate_NeedsAnExistingActiveGeneralService()
  {
    var missing = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _rates.CreateAsync(new ServiceCreateRequest("Barba", 15000, 30)));
    Assert.Contains("generalServiceId", missing.Errors.Keys);

    var unknown = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _rates.CreateAsync(new ServiceCreateRequest("Barba", 15000, 30, GeneralServiceId: Guid.NewGuid())));
    Assert.Contains("generalServiceId", unknown.Errors.Keys);

    var beard = await _catalog.CreateGeneralServiceAsync(new GeneralServiceCreateRequest(Barberia, "Barba"));
    await _catalog.UpdateGeneralServiceStatusAsync(beard.Id, new GeneralServiceStatusUpdateRequest(false));
    await Assert.ThrowsAsync<ConflictException>(() =>
        _rates.CreateAsync(new ServiceCreateRequest("Barba", 15000, 30, GeneralServiceId: beard.Id)));
  }

  [Fact]
  public async Task MovingARate_ChangesOnlyItsGeneralService()
  {
    var created = await _rates.CreateAsync(new ServiceCreateRequest("Barba clásica", 15000, 40, GeneralServiceId: Corte));
    var beard = await _catalog.CreateGeneralServiceAsync(new GeneralServiceCreateRequest(Barberia, "Barba"));

    var moved = await _rates.UpdateAsync(created.Id, new ServiceUpdateRequest("Barba clásica", 15000, 40, GeneralServiceId: beard.Id));

    Assert.Equal((created.Id, "Barba clásica", 15000, 40, true, 0), (moved.Id, moved.Name, moved.BasePrice, moved.BusinessPercentage, moved.IsActive, moved.LoyaltyPoints));
    Assert.Equal((beard.Id, "Barba", Barberia, "Barbería"), (moved.GeneralServiceId, moved.GeneralServiceName, moved.CategoryId, moved.CategoryName));
    Assert.Equal((1, 1), ((await _catalog.GetGeneralServiceAsync(beard.Id)).RateCount, (await _catalog.GetGeneralServiceAsync(beard.Id)).ActiveRateCount));

    // Omitting the general service keeps the current one (clients that do not know about it keep working).
    var kept = await _rates.UpdateAsync(created.Id, new ServiceUpdateRequest("Barba clásica", 16000, 40));
    Assert.Equal((beard.Id, 16000), (kept.GeneralServiceId, kept.BasePrice));
  }

  [Fact]
  public async Task TheRateList_CarriesItsGeneralServiceAndCategory()
  {
    await _rates.CreateAsync(new ServiceCreateRequest("Corte clásico", 25000, 30, GeneralServiceId: Corte));

    var rate = Assert.Single(await _rates.GetAllAsync());

    Assert.Equal((Corte, "Corte", true, Barberia, "Barbería"),
        (rate.GeneralServiceId, rate.GeneralServiceName, rate.GeneralServiceIsActive, rate.CategoryId, rate.CategoryName));
  }
}
