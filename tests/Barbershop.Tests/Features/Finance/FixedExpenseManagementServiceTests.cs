using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Finance.Admin;
using Barbershop.Infrastructure.Finance;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Features.Finance;

public sealed class FixedExpenseManagementServiceTests : IDisposable
{
  private readonly AppDbContext _dbContext;
  private readonly IAdminFixedExpensesService _service;

  public FixedExpenseManagementServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    _service = new FixedExpenseManagementService(_dbContext, TimeProvider.System);
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task CreateAsync_CreatesActiveFixedExpense()
  {
    var view = await _service.CreateAsync(new FixedExpenseCreateRequest("Arriendo", 1500000));

    Assert.NotEqual(Guid.Empty, view.Id);
    Assert.Equal("Arriendo", view.Name);
    Assert.Equal(1500000, view.DefaultAmount);
    Assert.True(view.IsActive);
    Assert.Single(await _dbContext.FixedExpenses.ToListAsync());
  }

  [Fact]
  public async Task CreateAsync_AllowsNullDefaultAmount()
  {
    var view = await _service.CreateAsync(new FixedExpenseCreateRequest("Varios", null));
    Assert.Null(view.DefaultAmount);
  }

  [Fact]
  public async Task CreateAsync_RejectsDuplicateNameCaseInsensitive()
  {
    await _service.CreateAsync(new FixedExpenseCreateRequest("Arriendo", 1000000));

    var exception = await Assert.ThrowsAsync<ConflictException>(() =>
        _service.CreateAsync(new FixedExpenseCreateRequest("  arriendo  ", 2000000)));

    Assert.Equal("A fixed expense with this name already exists.", exception.Message);
  }

  [Fact]
  public async Task CreateAsync_RejectsNegativeDefaultAmount()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(new FixedExpenseCreateRequest("Malo", -1)));

    Assert.Contains("defaultAmount", exception.Errors.Keys);
  }

  [Fact]
  public async Task UpdateAsync_ChangesNameAndAmount()
  {
    var created = await _service.CreateAsync(new FixedExpenseCreateRequest("Luz", 200000));

    var updated = await _service.UpdateAsync(created.Id, new FixedExpenseUpdateRequest("Energía", 250000));

    Assert.Equal("Energía", updated.Name);
    Assert.Equal(250000, updated.DefaultAmount);
  }

  [Fact]
  public async Task UpdateStatusAsync_Deactivates_ButKeepsInList()
  {
    var created = await _service.CreateAsync(new FixedExpenseCreateRequest("Agua", 80000));

    var updated = await _service.UpdateStatusAsync(created.Id, new FixedExpenseStatusUpdateRequest(false));
    Assert.False(updated.IsActive);

    var all = await _service.GetAllAsync();
    Assert.Contains(all, e => e.Id == created.Id);
  }

  [Fact]
  public async Task SoftDeleteAsync_RemovesFromListAndFreesName()
  {
    var created = await _service.CreateAsync(new FixedExpenseCreateRequest("Internet", 120000));

    await _service.SoftDeleteAsync(created.Id);

    var all = await _service.GetAllAsync();
    Assert.DoesNotContain(all, e => e.Id == created.Id);

    var recreated = await _service.CreateAsync(new FixedExpenseCreateRequest("Internet", 130000));
    Assert.NotEqual(created.Id, recreated.Id);
  }

  [Fact]
  public async Task GetByIdAsync_ThrowsForDeleted()
  {
    var created = await _service.CreateAsync(new FixedExpenseCreateRequest("Aseo", 50000));
    await _service.SoftDeleteAsync(created.Id);

    await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.GetByIdAsync(created.Id));
  }
}
