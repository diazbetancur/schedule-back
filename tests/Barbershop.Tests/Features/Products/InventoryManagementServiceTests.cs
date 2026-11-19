using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Products;
using Barbershop.Domain.Products;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Products;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Barbershop.Tests.Features.Products;

/// <summary>ADR-0020 stage 1: product inventory (stock, stock card, receipts, average cost, adjustments, alerts).</summary>
public sealed class InventoryManagementServiceTests : IDisposable
{
  private readonly AppDbContext _dbContext;
  private readonly IAdminInventoryService _inventory;
  private readonly Guid _adminId;
  private readonly Guid _categoryId;

  public InventoryManagementServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options;
    _dbContext = new AppDbContext(options);
    _dbContext.Database.EnsureCreated();
    var admin = new User("Ana Admin", "admin@example.com", "hash", DateTime.UtcNow);
    _dbContext.Users.Add(admin);
    var category = new ProductCategory("Ceras", 1, DateTime.UtcNow);
    _dbContext.ProductCategories.Add(category);
    _dbContext.SaveChanges();
    _adminId = admin.Id;
    _categoryId = category.Id;
    _inventory = new InventoryManagementService(_dbContext, new SteppingTimeProvider());
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task AnExistingProduct_StartsWithNoStockAndNoCost()
  {
    var product = SeedProduct("Cera mate", 35000);

    var item = await _inventory.GetAsync(product);

    Assert.Equal(0, item.OnHand);
    Assert.Null(item.AverageUnitCost);
    Assert.Null(item.UnitProfit);
    Assert.Null(item.MarginPercent);
    Assert.Null(item.StockValue);
    Assert.Null(item.MinStock);
    Assert.False(item.IsLowStock);
    Assert.False(await _dbContext.ProductStocks.AnyAsync());

    var summary = await _inventory.GetSummaryAsync();
    Assert.Equal(1, summary.ProductsWithoutCost);
    Assert.Equal(0, summary.StockValue);
  }

  [Fact]
  public async Task Receipts_ComputeTheWeightedAverageCost_AndTheUnitProfit()
  {
    var product = SeedProduct("Cera mate", 35000);

    var first = await Receive(product, 10, 20000);
    Assert.Equal(10, first.Item.OnHand);
    Assert.Equal(20000m, first.Item.AverageUnitCost);
    Assert.Equal("Receipt", first.Movement.Type);
    Assert.Equal(10, first.Movement.Quantity);
    Assert.Equal("Ana Admin", first.Movement.PerformedByName);

    var second = await Receive(product, 5, 23000);
    // (10 × 20 000 + 5 × 23 000) / 15 = 21 000
    Assert.Equal(15, second.Item.OnHand);
    Assert.Equal(21000m, second.Item.AverageUnitCost);
    Assert.Equal(14000m, second.Item.UnitProfit);
    Assert.Equal(40.0m, second.Item.MarginPercent);
    Assert.Equal(315000, second.Item.StockValue);
    Assert.Equal(15, second.Movement.OnHandAfter);
    Assert.Equal(21000m, second.Movement.AverageUnitCostAfter);
  }

  [Fact]
  public void TheAverage_FromZeroOrNegativeStock_IsTheReceiptCost_AndRoundsToFourDecimals()
  {
    Assert.Equal(1000m, ProductStock.NextAverageCost(0, null, 3, 1000));
    Assert.Equal(1000m, ProductStock.NextAverageCost(-2, 800m, 3, 1000)); // covers the shortfall first
    Assert.Equal(1000m, ProductStock.NextAverageCost(4, null, 3, 1000)); // units without a known cost
    Assert.Equal(1333.3333m, ProductStock.NextAverageCost(2, 1000m, 1, 2000));
  }

  [Fact]
  public async Task ARetriedReceipt_ReturnsTheStoredMovement_AndDifferentDataWithTheSameKeyIsAConflict()
  {
    var product = SeedProduct("Cera mate", 35000);
    var key = Guid.NewGuid();

    var first = await _inventory.ReceiveAsync(_adminId, product, new InventoryReceiptRequest(key, 4, 10000));
    var again = await _inventory.ReceiveAsync(_adminId, product, new InventoryReceiptRequest(key, 4, 10000));

    Assert.Equal(first.Movement.Id, again.Movement.Id);
    Assert.Equal(4, again.Item.OnHand);
    Assert.Single(await _dbContext.InventoryMovements.ToListAsync());

    var conflict = await Assert.ThrowsAsync<ConflictException>(() =>
        _inventory.ReceiveAsync(_adminId, product, new InventoryReceiptRequest(key, 5, 10000)));
    Assert.Equal(InventoryManagementService.IdempotencyConflictMessage, conflict.Message);
  }

  [Fact]
  public async Task AReceipt_CanAlsoCreateThePurchaseExpense()
  {
    var product = SeedProduct("Aceite para barba", 28000);

    var result = await _inventory.ReceiveAsync(
        _adminId, product,
        new InventoryReceiptRequest(Guid.NewGuid(), 6, 12500, Supplier: " Distribuidora Norte ", Reference: "F-102", CreateExpense: true));

    var expense = await _dbContext.ExpenseEntries.SingleAsync();
    Assert.Equal("Compra de mercancía: Aceite para barba", expense.Name);
    Assert.Equal(75000, expense.Amount);
    Assert.Equal(_adminId, expense.CreatedByUserId);
    Assert.Equal(expense.Id, result.Movement.ExpenseEntryId);
    Assert.Equal("Distribuidora Norte", result.Movement.Supplier);
    Assert.Equal("F-102", result.Movement.Reference);

    // Without the option nothing reaches Finance; a zero cost cannot create an expense.
    await Receive(product, 1, 12500);
    Assert.Equal(1, await _dbContext.ExpenseEntries.CountAsync());
    var zero = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _inventory.ReceiveAsync(_adminId, product, new InventoryReceiptRequest(Guid.NewGuid(), 1, 0, CreateExpense: true)));
    Assert.Equal(["createExpense"], zero.Errors.Keys);
  }

  [Fact]
  public async Task Receipts_AreValidated()
  {
    var product = SeedProduct("Cera mate", 35000);
    var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5)).AddDays(1);

    var invalid = await Assert.ThrowsAsync<ValidationProblemException>(() => _inventory.ReceiveAsync(
        _adminId, product,
        new InventoryReceiptRequest(null, 0, -1, tomorrow, new string('a', 121), new string('b', 121))));

    Assert.Equal(["idempotencyKey", "occurredOn", "quantity", "reference", "supplier", "unitCost"], invalid.Errors.Keys.Order());
    Assert.False(await _dbContext.InventoryMovements.AnyAsync());
  }

  [Fact]
  public async Task Adjustments_NeedAReason_KeepTheAverage_AndNeverGoBelowZero()
  {
    var product = SeedProduct("Cera mate", 35000);
    await Receive(product, 5, 20000);

    var missing = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _inventory.AdjustAsync(_adminId, product, new InventoryAdjustmentRequest(Guid.NewGuid(), "Out", 1, " ")));
    Assert.Equal(["reason"], missing.Errors.Keys);

    var damaged = await _inventory.AdjustAsync(
        _adminId, product, new InventoryAdjustmentRequest(Guid.NewGuid(), "out", 2, "Dos frascos rotos"));
    Assert.Equal(3, damaged.Item.OnHand);
    Assert.Equal(20000m, damaged.Item.AverageUnitCost);
    Assert.Equal("AdjustmentOut", damaged.Movement.Type);
    Assert.Equal(-2, damaged.Movement.Quantity);
    Assert.Equal("Dos frascos rotos", damaged.Movement.Reason);

    var tooMany = await Assert.ThrowsAsync<ConflictException>(() =>
        _inventory.AdjustAsync(_adminId, product, new InventoryAdjustmentRequest(Guid.NewGuid(), "Out", 4, "Conteo físico")));
    Assert.Equal(InventoryManagementService.NegativeAdjustmentMessage, tooMany.Message);

    var found = await _inventory.AdjustAsync(_adminId, product, new InventoryAdjustmentRequest(Guid.NewGuid(), "In", 1, "Conteo físico"));
    Assert.Equal(4, found.Item.OnHand);

    await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _inventory.AdjustAsync(_adminId, product, new InventoryAdjustmentRequest(Guid.NewGuid(), "Sideways", 1, "Conteo físico")));
  }

  [Fact]
  public async Task TheStock_AlwaysEqualsTheSignedSumOfTheMovements()
  {
    var product = SeedProduct("Cera mate", 35000);
    await Receive(product, 8, 15000);
    await _inventory.AdjustAsync(_adminId, product, new InventoryAdjustmentRequest(Guid.NewGuid(), "Out", 3, "Muestras"));
    await Receive(product, 2, 18000);
    await _inventory.AdjustAsync(_adminId, product, new InventoryAdjustmentRequest(Guid.NewGuid(), "In", 1, "Conteo físico"));

    var movements = await _dbContext.InventoryMovements.Where(movement => movement.ProductId == product).ToListAsync();
    var stock = await _dbContext.ProductStocks.SingleAsync(row => row.ProductId == product);
    Assert.Equal(movements.Sum(movement => movement.SignedQuantity), stock.OnHand);
    Assert.Equal(8, stock.OnHand);

    var card = await _inventory.GetMovementsAsync(product, new InventoryMovementListQuery());
    Assert.Equal(4, card.TotalCount);
    Assert.Equal(["AdjustmentIn", "Receipt", "AdjustmentOut", "Receipt"], card.Items.Select(movement => movement.Type));
    var receipts = await _inventory.GetMovementsAsync(product, new InventoryMovementListQuery(Type: "Receipt"));
    Assert.Equal(2, receipts.TotalCount);
  }

  [Fact]
  public async Task MinimumStock_DrivesTheLowStockAlert()
  {
    var low = SeedProduct("Cera mate", 35000);
    var fine = SeedProduct("Shampoo", 30000);
    await Receive(low, 2, 20000);
    await Receive(fine, 10, 15000);

    var set = await _inventory.SetMinStockAsync(low, new InventoryMinStockRequest(3));
    Assert.Equal(3, set.MinStock);
    Assert.True(set.IsLowStock);
    await _inventory.SetMinStockAsync(fine, new InventoryMinStockRequest(3));

    var onlyLow = await _inventory.ListAsync(new InventoryListQuery(LowStock: true));
    Assert.Equal([low], onlyLow.Items.Select(item => item.ProductId));
    Assert.Equal(1, (await _inventory.GetSummaryAsync()).LowStockCount);

    var cleared = await _inventory.SetMinStockAsync(low, new InventoryMinStockRequest(null));
    Assert.Null(cleared.MinStock);
    Assert.False(cleared.IsLowStock);
    Assert.Equal(0, (await _inventory.GetSummaryAsync()).LowStockCount);

    await Assert.ThrowsAsync<ValidationProblemException>(() => _inventory.SetMinStockAsync(low, new InventoryMinStockRequest(-1)));
  }

  [Fact]
  public async Task TheList_FiltersByText_AndHidesDeletedProducts()
  {
    var wax = SeedProduct("Cera mate", 35000);
    var deleted = SeedProduct("Cera vieja", 10000);
    var product = await _dbContext.Products.SingleAsync(candidate => candidate.Id == deleted);
    product.MarkDeleted(DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();

    var page = await _inventory.ListAsync(new InventoryListQuery(Q: "cera"));

    Assert.Equal([wax], page.Items.Select(item => item.ProductId));
    Assert.Equal("Ceras", page.Items[0].CategoryName);
    await Assert.ThrowsAsync<NotFoundException>(() => Receive(deleted, 1, 1000));
    await Assert.ThrowsAsync<NotFoundException>(() => _inventory.GetAsync(Guid.NewGuid()));
    // SONDA BUG-140: its stock card answers like its detail (404), not 200 with a page the screen cannot show.
    await Assert.ThrowsAsync<NotFoundException>(() => _inventory.GetMovementsAsync(deleted, new InventoryMovementListQuery()));
  }

  private Guid SeedProduct(string name, int price)
  {
    var product = new Product(_categoryId, name, null, price, 0, DateTime.UtcNow);
    _dbContext.Products.Add(product);
    _dbContext.SaveChanges();
    return product.Id;
  }

  /// <summary>Every reading is one second later, so the stock card has a strict order by time.</summary>
  private sealed class SteppingTimeProvider : TimeProvider
  {
    private DateTimeOffset _now = DateTimeOffset.UtcNow.AddHours(-1);

    public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
  }

  private Task<InventoryMovementResultView> Receive(Guid productId, int quantity, int unitCost)
      => _inventory.ReceiveAsync(_adminId, productId, new InventoryReceiptRequest(Guid.NewGuid(), quantity, unitCost));
}
