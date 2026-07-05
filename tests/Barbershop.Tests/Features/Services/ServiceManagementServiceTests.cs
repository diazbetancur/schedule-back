using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Services.Admin;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Features.Services;

public sealed class ServiceManagementServiceTests : IDisposable
{
  private readonly AppDbContext _dbContext;
  private readonly IAdminServicesService _service;

  public ServiceManagementServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    _service = new ServiceManagementService(_dbContext, TimeProvider.System);
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task CreateAsync_CreatesActiveService()
  {
    var view = await _service.CreateAsync(new ServiceCreateRequest("Corte de Cabello", 25000));

    Assert.NotEqual(Guid.Empty, view.Id);
    Assert.Equal("Corte de Cabello", view.Name);
    Assert.Equal(25000, view.BasePrice);
    Assert.True(view.IsActive);
    Assert.Single(await _dbContext.Services.ToListAsync());
  }

  [Fact]
  public async Task CreateAsync_RejectsDuplicateNameCaseInsensitive()
  {
    await _service.CreateAsync(new ServiceCreateRequest("Corte de Cabello", 25000));

    var exception = await Assert.ThrowsAsync<ConflictException>(() =>
        _service.CreateAsync(new ServiceCreateRequest("  corte de cabello  ", 30000)));

    Assert.Equal("A service with this name already exists.", exception.Message);
  }

  [Fact]
  public async Task CreateAsync_RejectsNegativePrice()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(new ServiceCreateRequest("Barba", -1)));

    Assert.Contains("basePrice", exception.Errors.Keys);
  }

  [Fact]
  public async Task UpdateAsync_ChangesNameAndPrice()
  {
    var created = await _service.CreateAsync(new ServiceCreateRequest("Barba", 10000));

    var updated = await _service.UpdateAsync(created.Id, new ServiceUpdateRequest("Barba Premium", 15000));

    Assert.Equal("Barba Premium", updated.Name);
    Assert.Equal(15000, updated.BasePrice);
  }

  [Fact]
  public async Task UpdateStatusAsync_Deactivates_ButKeepsInList()
  {
    var created = await _service.CreateAsync(new ServiceCreateRequest("Tinte", 40000));

    var updated = await _service.UpdateStatusAsync(created.Id, new ServiceStatusUpdateRequest(false));
    Assert.False(updated.IsActive);

    var all = await _service.GetAllAsync();
    Assert.Contains(all, s => s.Id == created.Id);
  }

  [Fact]
  public async Task SoftDeleteAsync_RemovesFromListAndFreesName()
  {
    var created = await _service.CreateAsync(new ServiceCreateRequest("Cejas", 8000));

    await _service.SoftDeleteAsync(created.Id);

    var all = await _service.GetAllAsync();
    Assert.DoesNotContain(all, s => s.Id == created.Id);

    var recreated = await _service.CreateAsync(new ServiceCreateRequest("Cejas", 9000));
    Assert.NotEqual(created.Id, recreated.Id);
  }

  [Fact]
  public async Task GetByIdAsync_ThrowsForDeleted()
  {
    var created = await _service.CreateAsync(new ServiceCreateRequest("Mascarilla", 12000));
    await _service.SoftDeleteAsync(created.Id);

    await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.GetByIdAsync(created.Id));
  }
}
