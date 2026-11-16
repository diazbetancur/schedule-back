using Barbershop.Application.Common.Paging;

namespace Barbershop.Application.Products;

// ADR-0020 stage 1: product inventory. Stock, an append-only stock card, receipts with cost (and an optional purchase
// expense), weighted average cost, manual adjustments with a reason and a low stock threshold. Since stage 3 the
// approved bills sell (Sale movements) and the profit by product comes from those real sales.

/// <param name="AverageUnitCost">Weighted average in pesos (4 decimals); null when no receipt has given it a cost.</param>
/// <param name="UnitProfit">Price minus average cost; null without a cost.</param>
/// <param name="MarginPercent">Unit profit over the price, in percent (1 decimal); null without a cost or with price 0.</param>
/// <param name="StockValue">Units on hand (if positive) times the average cost, rounded to pesos; null without a cost.</param>
/// <param name="Committed">ADR-0020 §6: units of closed bills not approved yet (they left the shop, not the stock yet).</param>
/// <param name="Available">On hand minus committed; the low stock alert compares it with the minimum.</param>
public sealed record InventoryItemView(
    Guid ProductId,
    string Name,
    Guid CategoryId,
    string CategoryName,
    bool IsActive,
    int Price,
    int OnHand,
    decimal? AverageUnitCost,
    int? MinStock,
    bool IsLowStock,
    decimal? UnitProfit,
    decimal? MarginPercent,
    long? StockValue,
    DateTime? UpdatedAt,
    int Committed = 0,
    int Available = 0);

public sealed record InventorySummaryView(int LowStockCount, int NegativeStockCount, long StockValue, int ProductsWithoutCost);

/// <param name="Quantity">Signed: positive in, negative out.</param>
public sealed record InventoryMovementView(
    Guid Id,
    Guid ProductId,
    string Type,
    int Quantity,
    decimal? UnitCost,
    int OnHandAfter,
    decimal? AverageUnitCostAfter,
    DateOnly OccurredOn,
    string? Reason,
    string? Supplier,
    string? Reference,
    Guid? ExpenseEntryId,
    string PerformedByName,
    DateTime CreatedAt);

/// <param name="LowStock">true: only products at or below their minimum stock.</param>
/// <param name="Status">Active, Inactive or empty for both, as in the product list.</param>
public sealed record InventoryListQuery(
    string? Q = null,
    Guid? CategoryId = null,
    bool? LowStock = null,
    string? Status = null,
    int? Page = null,
    int? PageSize = null);

public sealed record InventoryMovementListQuery(
    DateOnly? From = null,
    DateOnly? To = null,
    string? Type = null,
    int? Page = null,
    int? PageSize = null);

/// <param name="OccurredOn">Bogotá date of the receipt; today when omitted. Never in the future.</param>
/// <param name="CreateExpense">Also records the purchase as an expense in Finance (ADR-0020 Q13).</param>
public sealed record InventoryReceiptRequest(
    Guid? IdempotencyKey,
    int? Quantity,
    int? UnitCost,
    DateOnly? OccurredOn = null,
    string? Supplier = null,
    string? Reference = null,
    bool CreateExpense = false);

/// <param name="Direction">In or Out.</param>
public sealed record InventoryAdjustmentRequest(Guid? IdempotencyKey, string? Direction, int? Quantity, string? Reason);

/// <param name="MinStock">null clears the threshold.</param>
public sealed record InventoryMinStockRequest(int? MinStock);

/// <summary>The stock line and the movement just recorded (or the one a retried request already stored).</summary>
public sealed record InventoryMovementResultView(InventoryItemView Item, InventoryMovementView Movement);

/// <summary>
/// Real sales of one product in the range, from the incomes of approved bills (ADR-0020 stage 3), and its rotation (stage
/// 5): units available now, daily average sold in the range and days of coverage (available ÷ daily average; null when
/// nothing sold).
/// </summary>
/// <param name="Cost">Units times the average cost copied at each sale; only units with a registered cost count.</param>
/// <param name="UnitsWithoutCost">Units sold when the product had no cost registered: their profit cannot be known.</param>
public sealed record InventorySalesItemView(
    Guid ProductId,
    string Name,
    int Units,
    long Revenue,
    long Cost,
    long Profit,
    decimal? MarginPercent,
    int UnitsWithoutCost,
    int Available = 0,
    decimal? DailyUnits = null,
    decimal? CoverageDays = null,
    bool IsLowStock = false);

public sealed record InventorySalesView(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<InventorySalesItemView> Items,
    int Units,
    long Revenue,
    long Cost,
    long Profit,
    int UnitsWithoutCost);

public sealed record InventorySalesQuery(DateOnly? From = null, DateOnly? To = null);

public interface IAdminInventoryService
{
    Task<InventorySalesView> GetSalesAsync(InventorySalesQuery query, CancellationToken cancellationToken = default);

    Task<PagedResult<InventoryItemView>> ListAsync(InventoryListQuery query, CancellationToken cancellationToken = default);

    Task<InventorySummaryView> GetSummaryAsync(CancellationToken cancellationToken = default);

    Task<InventoryItemView> GetAsync(Guid productId, CancellationToken cancellationToken = default);

    Task<PagedResult<InventoryMovementView>> GetMovementsAsync(
        Guid productId, InventoryMovementListQuery query, CancellationToken cancellationToken = default);

    Task<InventoryMovementResultView> ReceiveAsync(
        Guid currentUserId, Guid productId, InventoryReceiptRequest request, CancellationToken cancellationToken = default);

    Task<InventoryMovementResultView> AdjustAsync(
        Guid currentUserId, Guid productId, InventoryAdjustmentRequest request, CancellationToken cancellationToken = default);

    Task<InventoryItemView> SetMinStockAsync(Guid productId, InventoryMinStockRequest request, CancellationToken cancellationToken = default);
}
