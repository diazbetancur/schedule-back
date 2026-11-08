using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Services.Admin;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Barbershop.Domain.Services;

namespace Barbershop.Tests.Features.Services;

public sealed class ServiceManagementServiceTests : IDisposable
{
  private readonly AppDbContext _dbContext;
  private readonly IAdminServicesService _service;

  public ServiceManagementServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options;

    _dbContext = new AppDbContext(options);
    _dbContext.Database.EnsureCreated(); // seeds the default category and general service (ADR-0016 D4)
    _service = new ServiceManagementService(_dbContext, TimeProvider.System, new NullCurrentUserAccessor());
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task CreateAsync_CreatesActiveService()
  {
    var view = await _service.CreateAsync(new ServiceCreateRequest("Corte de Cabello", 25000, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));

    Assert.NotEqual(Guid.Empty, view.Id);
    Assert.Equal("Corte de Cabello", view.Name);
    Assert.Equal(25000, view.BasePrice);
    Assert.True(view.IsActive);
    Assert.Single(await _dbContext.Services.ToListAsync());
  }

  [Fact]
  public async Task CreateAsync_RejectsDuplicateNameCaseInsensitive()
  {
    await _service.CreateAsync(new ServiceCreateRequest("Corte de Cabello", 25000, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));

    var exception = await Assert.ThrowsAsync<ConflictException>(() =>
        _service.CreateAsync(new ServiceCreateRequest("  corte de cabello  ", 30000, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId)));

    Assert.Equal("A service with this name already exists.", exception.Message);
  }

  [Fact]
  public async Task CreateAsync_RejectsNegativePrice()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(new ServiceCreateRequest("Barba", -1, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId)));

    Assert.Contains("basePrice", exception.Errors.Keys);
  }

  [Fact]
  public async Task UpdateAsync_ChangesNameAndPrice()
  {
    var created = await _service.CreateAsync(new ServiceCreateRequest("Barba", 10000, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));

    var updated = await _service.UpdateAsync(created.Id, new ServiceUpdateRequest("Barba Premium", 15000));

    Assert.Equal("Barba Premium", updated.Name);
    Assert.Equal(15000, updated.BasePrice);
  }

  [Fact]
  public async Task UpdateAsync_KeepingSameName_Succeeds()
  {
    var created = await _service.CreateAsync(new ServiceCreateRequest("Corte", 20000, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));

    var updated = await _service.UpdateAsync(created.Id, new ServiceUpdateRequest("Corte", 25000));

    Assert.Equal("Corte", updated.Name);
    Assert.Equal(25000, updated.BasePrice);
  }

  [Fact]
  public async Task UpdateAsync_ToAnotherServicesName_ThrowsConflict()
  {
    await _service.CreateAsync(new ServiceCreateRequest("Corte", 20000, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));
    var barba = await _service.CreateAsync(new ServiceCreateRequest("Barba", 10000, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));

    var exception = await Assert.ThrowsAsync<ConflictException>(() =>
        _service.UpdateAsync(barba.Id, new ServiceUpdateRequest("corte", 15000)));

    Assert.Equal("A service with this name already exists.", exception.Message);
  }

  [Fact]
  public async Task UpdateStatusAsync_Deactivates_ButKeepsInList()
  {
    var created = await _service.CreateAsync(new ServiceCreateRequest("Tinte", 40000, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));

    var updated = await _service.UpdateStatusAsync(created.Id, new ServiceStatusUpdateRequest(false));
    Assert.False(updated.IsActive);

    var all = await _service.GetAllAsync();
    Assert.Contains(all, s => s.Id == created.Id);
  }

  [Fact]
  public async Task SoftDeleteAsync_RemovesFromListAndFreesName()
  {
    var created = await _service.CreateAsync(new ServiceCreateRequest("Cejas", 8000, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));

    await _service.SoftDeleteAsync(created.Id);

    var all = await _service.GetAllAsync();
    Assert.DoesNotContain(all, s => s.Id == created.Id);

    var recreated = await _service.CreateAsync(new ServiceCreateRequest("Cejas", 9000, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));
    Assert.NotEqual(created.Id, recreated.Id);
  }

  [Fact]
  public async Task GetByIdAsync_ThrowsForDeleted()
  {
    var created = await _service.CreateAsync(new ServiceCreateRequest("Mascarilla", 12000, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));
    await _service.SoftDeleteAsync(created.Id);

    await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.GetByIdAsync(created.Id));
  }

  [Fact]
  public async Task CreateAsync_DefaultsBusinessPercentageToZero()
  {
    var view = await _service.CreateAsync(new ServiceCreateRequest("Corte", 20000, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));

    Assert.Equal(0, view.BusinessPercentage);
  }

  [Fact]
  public async Task CreateAsync_StoresExplicitBusinessPercentage()
  {
    var view = await _service.CreateAsync(new ServiceCreateRequest("Corte", 20000, BusinessPercentage: 40, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));

    Assert.Equal(40, view.BusinessPercentage);
  }

  [Fact]
  public async Task CreateAsync_RejectsBusinessPercentageAboveHundred()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(new ServiceCreateRequest("Corte", 20000, BusinessPercentage: 101, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId)));

    Assert.Contains("businessPercentage", exception.Errors.Keys);
  }

  [Fact]
  public async Task CreateAsync_RejectsNegativeBusinessPercentage()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(new ServiceCreateRequest("Corte", 20000, BusinessPercentage: -1, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId)));

    Assert.Contains("businessPercentage", exception.Errors.Keys);
  }

  [Fact]
  public async Task UpdateAsync_ChangesBusinessPercentage()
  {
    var created = await _service.CreateAsync(new ServiceCreateRequest("Barba", 10000, BusinessPercentage: 10, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));

    var updated = await _service.UpdateAsync(created.Id, new ServiceUpdateRequest("Barba", 10000, BusinessPercentage: 60));

    Assert.Equal(60, updated.BusinessPercentage);
  }
}
