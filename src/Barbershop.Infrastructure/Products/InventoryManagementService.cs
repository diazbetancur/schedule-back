using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Common.Paging;
using Barbershop.Application.Products;
using Barbershop.Domain.Common;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Products;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Barbershop.Infrastructure.Products;

/// <summary>
/// Product inventory (ADR-0020 stage 1). Every movement runs in a transaction that first locks the product row
/// (<see cref="ProductCatalogLocks.LockProductAsync"/>, the same lock a product deletion takes), then reads or creates its
/// stock row, records the append-only movement and updates the projection. Two movements of the same product are
/// therefore serialized, and the projection always equals the signed sum of the movements.
/// </summary>
internal sealed class InventoryManagementService : IAdminInventoryService
{
    internal const string ProductNotFoundMessage = "El producto no existe o fue eliminado.";
    internal const string NegativeAdjustmentMessage =
        "El ajuste dejaría la existencia por debajo de cero. Revisa la cantidad: solo puedes retirar las unidades que hay.";
    internal const string IdempotencyConflictMessage =
        "Ya se registró un movimiento con esta clave y datos distintos. Recarga la página e intenta de nuevo.";
    private const string ExpenseNamePrefix = "Compra de mercancía: ";

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public InventoryManagementService(AppDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<PagedResult<InventoryItemView>> ListAsync(InventoryListQuery listQuery, CancellationToken cancellationToken = default)
    {
        var page = PageRequest.Create(listQuery.Page, listQuery.PageSize);
        var isActive = CatalogStatusFilter.Parse(listQuery.Status);

        var rows =
            from product in _dbContext.Products.AsNoTracking()
            where !product.IsDeleted
            join category in _dbContext.ProductCategories.AsNoTracking() on product.CategoryId equals category.Id
            join stockRow in _dbContext.ProductStocks.AsNoTracking() on product.Id equals stockRow.ProductId into stocks
            from stock in stocks.DefaultIfEmpty()
            select new { Product = product, Category = category, Stock = stock };

        if (listQuery.CategoryId is { } categoryId)
        {
            rows = rows.Where(row => row.Product.CategoryId == categoryId);
        }

        if (ListFilters.Text(listQuery.Q) is { } text)
        {
            var upper = text.ToUpperInvariant();
            rows = rows.Where(row => row.Product.Name.ToUpper().Contains(upper));
        }

        if (isActive is { } active)
        {
            rows = rows.Where(row => row.Product.IsActive == active);
        }

        if (listQuery.LowStock == true)
        {
            rows = rows.Where(row => row.Stock != null && row.Stock.MinStock != null && row.Stock.OnHand <= row.Stock.MinStock);
        }

        // Same catalog order as the product list, so both screens read alike.
        var ordered = rows
            .OrderBy(row => row.Category.SortOrder)
            .ThenBy(row => row.Category.Name)
            .ThenBy(row => row.Product.SortOrder)
            .ThenBy(row => row.Product.Name)
            .ThenBy(row => row.Product.Id);

        return await ordered.ToPagedResultAsync(
            page,
            row => Map(row.Product, row.Category.Name, row.Stock),
            cancellationToken);
    }

    public async Task<InventorySummaryView> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var rows = await (
                from product in _dbContext.Products.AsNoTracking()
                where !product.IsDeleted
                join stockRow in _dbContext.ProductStocks.AsNoTracking() on product.Id equals stockRow.ProductId into stocks
                from stock in stocks.DefaultIfEmpty()
                select new
                {
                    OnHand = stock == null ? 0 : stock.OnHand,
                    MinStock = stock == null ? null : stock.MinStock,
                    AverageUnitCost = stock == null ? null : stock.AverageUnitCost,
                })
            .ToListAsync(cancellationToken);

        return new InventorySummaryView(
            rows.Count(row => row.MinStock is { } min && row.OnHand <= min),
            rows.Count(row => row.OnHand < 0),
            rows.Sum(row => StockValue(row.OnHand, row.AverageUnitCost) ?? 0),
            rows.Count(row => row.AverageUnitCost is null));
    }

    public async Task<InventoryItemView> GetAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        var product = await _dbContext.Products.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == productId && !candidate.IsDeleted, cancellationToken)
            ?? throw new NotFoundException(ProductNotFoundMessage);
        return await MapAsync(product, cancellationToken);
    }

    public async Task<PagedResult<InventoryMovementView>> GetMovementsAsync(
        Guid productId, InventoryMovementListQuery listQuery, CancellationToken cancellationToken = default)
    {
        var page = PageRequest.Create(listQuery.Page, listQuery.PageSize);
        // A stock card is read whole when no dates are given (it must add up); a given range is still limited to 30 days.
        var range = HistoryRange.ResolveOptional(listQuery.From, listQuery.To);
        var type = ListFilters.ParseEnum<InventoryMovementType>(listQuery.Type, "type");

        if (!await _dbContext.Products.AsNoTracking().AnyAsync(product => product.Id == productId, cancellationToken))
        {
            throw new NotFoundException(ProductNotFoundMessage);
        }

        var query = _dbContext.InventoryMovements.AsNoTracking().Where(movement => movement.ProductId == productId);
        if (range is not null)
        {
            query = query.Where(movement => movement.OccurredOn >= range.From && movement.OccurredOn <= range.To);
        }

        if (type is { } movementType)
        {
            query = query.Where(movement => movement.Type == movementType);
        }

        var ordered = query.OrderByDescending(movement => movement.CreatedAt).ThenByDescending(movement => movement.Id);
        return await ordered.ToPagedResultAsync(
            page,
            async (movements, ct) => (IReadOnlyList<InventoryMovementView>)await MapMovementsAsync(movements, ct),
            cancellationToken);
    }

    public async Task<InventoryMovementResultView> ReceiveAsync(
        Guid currentUserId, Guid productId, InventoryReceiptRequest request, CancellationToken cancellationToken = default)
    {
        var today = TodayInBogota();
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        RequireKey(request.IdempotencyKey, errors);
        ValidateQuantity(request.Quantity, errors);
        if (request.UnitCost is not { } unitCost || unitCost < 0 || unitCost > ProductStock.MaxUnitCost)
        {
            errors["unitCost"] = [$"Escribe el costo por unidad en pesos, entre 0 y 100.000.000."];
        }

        var occurredOn = request.OccurredOn ?? today;
        if (occurredOn > today)
        {
            errors["occurredOn"] = ["La fecha de la entrada no puede ser futura."];
        }

        var supplier = Trimmed(request.Supplier);
        var reference = Trimmed(request.Reference);
        if (supplier is { Length: > ProductStock.MaxSupplierLength })
        {
            errors["supplier"] = [$"El proveedor admite como máximo {ProductStock.MaxSupplierLength} caracteres."];
        }

        if (reference is { Length: > ProductStock.MaxReferenceLength })
        {
            errors["reference"] = [$"La referencia admite como máximo {ProductStock.MaxReferenceLength} caracteres."];
        }

        var expenseAmount = (long)(request.Quantity ?? 0) * (request.UnitCost ?? 0);
        if (request.CreateExpense && errors.Count == 0)
        {
            if (expenseAmount <= 0)
            {
                errors["createExpense"] = ["Para crear el gasto de compra, el costo por unidad debe ser mayor que 0."];
            }
            else if (expenseAmount > int.MaxValue)
            {
                errors["createExpense"] = ["El total de la compra es demasiado alto para registrarlo como un solo gasto."];
            }
        }

        ThrowIfAny(errors);
        var key = request.IdempotencyKey!.Value;

        return await RecordAsync(
            productId,
            key,
            existing => existing.Type == InventoryMovementType.Receipt
                && existing.Quantity == request.Quantity
                && existing.UnitCost == request.UnitCost
                && existing.OccurredOn == occurredOn
                && (existing.ExpenseEntryId is not null) == request.CreateExpense,
            (product, stock, utcNow) =>
            {
                Guid? expenseId = null;
                if (request.CreateExpense)
                {
                    var name = (ExpenseNamePrefix + product.Name).Trim();
                    var expense = new ExpenseEntry(
                        null, name.Length > 120 ? name[..120] : name, (int)expenseAmount, occurredOn, currentUserId, utcNow);
                    _dbContext.ExpenseEntries.Add(expense);
                    expenseId = expense.Id;
                }

                return stock.Receive(
                    request.Quantity!.Value, request.UnitCost!.Value, occurredOn, supplier, reference, expenseId, key,
                    currentUserId, utcNow);
            },
            cancellationToken);
    }

    public async Task<InventoryMovementResultView> AdjustAsync(
        Guid currentUserId, Guid productId, InventoryAdjustmentRequest request, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        RequireKey(request.IdempotencyKey, errors);
        ValidateQuantity(request.Quantity, errors);
        var type = ListFilters.ParseOption(request.Direction, "direction", "In", "Out") switch
        {
            "In" => InventoryMovementType.AdjustmentIn,
            "Out" => InventoryMovementType.AdjustmentOut,
            _ => (InventoryMovementType?)null,
        };
        if (type is null)
        {
            errors["direction"] = ["Indica si el ajuste suma (In) o resta (Out) unidades."];
        }

        var reason = Trimmed(request.Reason);
        if (reason is null || reason.Length < ProductStock.MinReasonLength || reason.Length > ProductStock.MaxReasonLength)
        {
            errors["reason"] = [$"El motivo es obligatorio y debe tener entre {ProductStock.MinReasonLength} y {ProductStock.MaxReasonLength} caracteres."];
        }

        ThrowIfAny(errors);
        var key = request.IdempotencyKey!.Value;
        var today = TodayInBogota();

        return await RecordAsync(
            productId,
            key,
            existing => existing.Type == type && existing.Quantity == request.Quantity && existing.Reason == reason,
            (_, stock, utcNow) =>
            {
                if (type == InventoryMovementType.AdjustmentOut && stock.OnHand - request.Quantity!.Value < 0)
                {
                    throw new ConflictException(NegativeAdjustmentMessage);
                }

                return stock.Adjust(type!.Value, request.Quantity!.Value, reason!, today, key, currentUserId, utcNow);
            },
            cancellationToken);
    }

    public async Task<InventoryItemView> SetMinStockAsync(
        Guid productId, InventoryMinStockRequest request, CancellationToken cancellationToken = default)
    {
        if (request.MinStock is < 0 or > ProductStock.MaxQuantity)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["minStock"] = [$"El stock mínimo debe estar entre 0 y 100.000, o vacío para no usar alerta."]
            });
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var product = await LockLiveProductAsync(productId, cancellationToken);
        var utcNow = UtcNow();
        var stock = await LoadOrCreateStockAsync(productId, utcNow, cancellationToken);
        stock.SetMinStock(request.MinStock, utcNow);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await MapAsync(product, cancellationToken);
    }

    // ── Movements ───────────────────────────────────────────────────────────────

    private async Task<InventoryMovementResultView> RecordAsync(
        Guid productId,
        Guid idempotencyKey,
        Func<InventoryMovement, bool> sameRequest,
        Func<Product, ProductStock, DateTime, InventoryMovement> apply,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var product = await LockLiveProductAsync(productId, cancellationToken);

            // A retried request (same key) returns what it stored; the product lock makes the check race-free.
            var existing = await _dbContext.InventoryMovements.AsNoTracking()
                .SingleOrDefaultAsync(movement => movement.IdempotencyKey == idempotencyKey, cancellationToken);
            if (existing is not null)
            {
                if (existing.ProductId != productId || !sameRequest(existing))
                {
                    throw new ConflictException(IdempotencyConflictMessage);
                }

                return new InventoryMovementResultView(
                    await MapAsync(product, cancellationToken), (await MapMovementsAsync([existing], cancellationToken))[0]);
            }

            var utcNow = UtcNow();
            var stock = await LoadOrCreateStockAsync(productId, utcNow, cancellationToken);
            InventoryMovement movement;
            try
            {
                movement = apply(product, stock, utcNow);
            }
            catch (InvalidOperationException)
            {
                throw new ConflictException(NegativeAdjustmentMessage);
            }

            _dbContext.InventoryMovements.Add(movement);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new InventoryMovementResultView(
                await MapAsync(product, cancellationToken), (await MapMovementsAsync([movement], cancellationToken))[0]);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            _dbContext.ChangeTracker.Clear();
            throw new ConflictException(IdempotencyConflictMessage);
        }
    }

    private async Task<Product> LockLiveProductAsync(Guid productId, CancellationToken cancellationToken)
    {
        await _dbContext.LockProductAsync(productId, cancellationToken);
        return await _dbContext.Products.AsNoTracking()
            .SingleOrDefaultAsync(product => product.Id == productId && !product.IsDeleted, cancellationToken)
            ?? throw new NotFoundException(ProductNotFoundMessage);
    }

    private async Task<ProductStock> LoadOrCreateStockAsync(Guid productId, DateTime utcNow, CancellationToken cancellationToken)
    {
        var stock = await _dbContext.ProductStocks.SingleOrDefaultAsync(row => row.ProductId == productId, cancellationToken);
        if (stock is null)
        {
            stock = new ProductStock(productId, utcNow);
            _dbContext.ProductStocks.Add(stock);
        }

        return stock;
    }

    // ── Mapping ─────────────────────────────────────────────────────────────────

    private async Task<InventoryItemView> MapAsync(Product product, CancellationToken cancellationToken)
    {
        var categoryName = await _dbContext.ProductCategories.AsNoTracking()
            .Where(category => category.Id == product.CategoryId)
            .Select(category => category.Name)
            .SingleAsync(cancellationToken);
        var stock = await _dbContext.ProductStocks.AsNoTracking()
            .SingleOrDefaultAsync(row => row.ProductId == product.Id, cancellationToken);
        return Map(product, categoryName, stock);
    }

    private static InventoryItemView Map(Product product, string categoryName, ProductStock? stock)
    {
        var onHand = stock?.OnHand ?? 0;
        var cost = stock?.AverageUnitCost;
        decimal? unitProfit = cost is { } average ? product.Price - average : null;
        decimal? margin = unitProfit is { } profit && product.Price > 0
            ? Math.Round(profit * 100 / product.Price, 1, MidpointRounding.AwayFromZero)
            : null;
        return new InventoryItemView(
            product.Id,
            product.Name,
            product.CategoryId,
            categoryName,
            product.IsActive,
            product.Price,
            onHand,
            cost,
            stock?.MinStock,
            stock?.IsLow ?? false,
            unitProfit,
            margin,
            StockValue(onHand, cost),
            stock?.UpdatedAt);
    }

    private static long? StockValue(int onHand, decimal? averageUnitCost)
        => averageUnitCost is { } cost
            ? (long)Math.Round(Math.Max(onHand, 0) * cost, 0, MidpointRounding.AwayFromZero)
            : null;

    private async Task<IReadOnlyList<InventoryMovementView>> MapMovementsAsync(
        IReadOnlyList<InventoryMovement> movements, CancellationToken cancellationToken)
    {
        var userIds = movements.Select(movement => movement.PerformedByUserId).Distinct().ToArray();
        var names = await _dbContext.Users.AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.FullName, cancellationToken);
        return movements
            .Select(movement => new InventoryMovementView(
                movement.Id,
                movement.ProductId,
                movement.Type.ToString(),
                movement.SignedQuantity,
                movement.UnitCost,
                movement.OnHandAfter,
                movement.AverageUnitCostAfter,
                movement.OccurredOn,
                movement.Reason,
                movement.Supplier,
                movement.Reference,
                movement.ExpenseEntryId,
                names.TryGetValue(movement.PerformedByUserId, out var name) ? name : string.Empty,
                movement.CreatedAt))
            .ToArray();
    }

    // ── Validation ──────────────────────────────────────────────────────────────

    private static void RequireKey(Guid? key, Dictionary<string, string[]> errors)
    {
        if (key is null || key == Guid.Empty)
        {
            errors["idempotencyKey"] = ["Falta la clave del movimiento. Recarga la página e intenta de nuevo."];
        }
    }

    private static void ValidateQuantity(int? quantity, Dictionary<string, string[]> errors)
    {
        if (quantity is not { } value || value < 1 || value > ProductStock.MaxQuantity)
        {
            errors["quantity"] = [$"La cantidad debe ser un número entero entre 1 y 100.000."];
        }
    }

    private static void ThrowIfAny(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private DateOnly TodayInBogota() => DateOnly.FromDateTime(BogotaClock.ToLocal(UtcNow()));
}
