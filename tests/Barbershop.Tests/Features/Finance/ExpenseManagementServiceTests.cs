using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Finance.Admin;
using Barbershop.Domain.Finance;
using Barbershop.Infrastructure.Finance;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Features.Finance;

public sealed class ExpenseManagementServiceTests : IDisposable
{
  private readonly AppDbContext _dbContext;
  private readonly IAdminExpensesService _service;
  private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));

  public ExpenseManagementServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    _service = new ExpenseManagementService(_dbContext, TimeProvider.System);
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task CreateAsync_LinkedToFixedExpense_SnapshotsMasterName()
  {
    var fixedExpense = await AddFixedExpenseAsync("Arriendo");
    var userId = Guid.NewGuid();

    var view = await _service.CreateAsync(userId, new ExpenseEntryCreateRequest(
        fixedExpense.Id, "ignored by server", 1500000, Today));

    Assert.Equal(fixedExpense.Id, view.FixedExpenseId);
    Assert.Equal("Arriendo", view.Name);
    Assert.Equal(1500000, view.Amount);

    var stored = await _dbContext.ExpenseEntries.SingleAsync();
    Assert.Equal(userId, stored.CreatedByUserId);
  }

  [Fact]
  public async Task CreateAsync_AdHoc_UsesTypedName()
  {
    var view = await _service.CreateAsync(Guid.NewGuid(), new ExpenseEntryCreateRequest(
        null, "Insumos varios", 30000, Today));

    Assert.Null(view.FixedExpenseId);
    Assert.Equal("Insumos varios", view.Name);
  }

  [Fact]
  public async Task CreateAsync_UnknownFixedExpense_ThrowsValidation()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(Guid.NewGuid(), new ExpenseEntryCreateRequest(
            Guid.NewGuid(), "x", 10000, Today)));

    Assert.Contains("fixedExpenseId", exception.Errors.Keys);
  }

  [Fact]
  public async Task CreateAsync_AdHocMissingName_ThrowsValidation()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(Guid.NewGuid(), new ExpenseEntryCreateRequest(
            null, " ", 10000, Today)));

    Assert.Contains("name", exception.Errors.Keys);
  }

  [Fact]
  public async Task CreateAsync_NegativeAmount_ThrowsValidation()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(Guid.NewGuid(), new ExpenseEntryCreateRequest(
            null, "Insumos", -1, Today)));

    Assert.Contains("amount", exception.Errors.Keys);
  }

  [Fact]
  public async Task CreateAsync_FutureDate_ThrowsValidation()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(Guid.NewGuid(), new ExpenseEntryCreateRequest(
            null, "Insumos", 10000, Today.AddDays(1))));

    Assert.Contains("occurredOn", exception.Errors.Keys);
  }

  [Fact]
  public async Task SoftDeleteAsync_ExcludesFromGet()
  {
    var created = await _service.CreateAsync(Guid.NewGuid(), new ExpenseEntryCreateRequest(
        null, "Insumos", 10000, Today));

    await _service.SoftDeleteAsync(created.Id);

    var list = await _service.GetAsync(Today.Year, Today.Month);
    Assert.DoesNotContain(list, e => e.Id == created.Id);
  }

  [Fact]
  public async Task GetAsync_FiltersByMonth()
  {
    var firstOfThisMonth = new DateOnly(Today.Year, Today.Month, 1);
    var lastMonthDay = firstOfThisMonth.AddDays(-1);

    await _service.CreateAsync(Guid.NewGuid(), new ExpenseEntryCreateRequest(null, "AA", 10000, firstOfThisMonth));
    await _service.CreateAsync(Guid.NewGuid(), new ExpenseEntryCreateRequest(null, "BB", 20000, lastMonthDay));

    var thisMonth = await _service.GetAsync(firstOfThisMonth.Year, firstOfThisMonth.Month);
    Assert.Single(thisMonth);
    Assert.Equal("AA", thisMonth[0].Name);
  }

  private async Task<FixedExpense> AddFixedExpenseAsync(string name)
  {
    var item = new FixedExpense(name, 1000000, DateTime.UtcNow);
    _dbContext.FixedExpenses.Add(item);
    await _dbContext.SaveChangesAsync();
    return item;
  }
}
