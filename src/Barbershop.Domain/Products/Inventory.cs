using Barbershop.Domain.Common;

namespace Barbershop.Domain.Products;

/// <summary>
/// Stock of one product (ADR-0020, stage 1): a projection of its <see cref="InventoryMovement"/> rows, updated in the same
/// transaction as each movement. Created on the first movement or stock threshold; a product without a row has no stock
/// and no known cost (existing products start that way, nothing is made up). <see cref="OnHand"/> may become negative
/// once sales exist (ADR-0020 P10).
/// </summary>
public sealed class ProductStock
{
  public const int MaxQuantity = 100_000;
  public const int MaxUnitCost = 100_000_000;
  public const int MinReasonLength = 3;
  public const int MaxReasonLength = 500;
  public const int MaxSupplierLength = 120;
  public const int MaxReferenceLength = 120;

  private ProductStock()
  {
  }

  public ProductStock(Guid productId, DateTime createdAt)
  {
    ProductId = productId == Guid.Empty ? throw new ArgumentException("A stock row needs its product.", nameof(productId)) : productId;
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
  }

  public Guid ProductId { get; private set; }

  /// <summary>Units in stock: the signed sum of the product's movements.</summary>
  public int OnHand { get; private set; }

  /// <summary>Weighted average unit cost in pesos (ADR-0020 Q14); null while no receipt has given it a cost.</summary>
  public decimal? AverageUnitCost { get; private set; }

  /// <summary>Optional threshold of the low stock alert (ADR-0020 Q21).</summary>
  public int? MinStock { get; private set; }

  public DateTime CreatedAt { get; private set; }
  public DateTime? UpdatedAt { get; private set; }

  public bool IsLow => MinStock is { } min && OnHand <= min;

  /// <summary>
  /// A merchandise receipt with its unit cost. The average is weighted with the units on hand; when the stock is zero or
  /// negative (or had no cost yet) the receipt first covers the shortfall and the average is the receipt's cost alone.
  /// </summary>
  public InventoryMovement Receive(
      int quantity, int unitCost, DateOnly occurredOn, string? supplier, string? reference, Guid? expenseEntryId,
      Guid idempotencyKey, Guid performedByUserId, DateTime utcNow)
  {
    EnsureQuantity(quantity);
    DomainValidation.EnsureRange(unitCost, 0, MaxUnitCost, nameof(unitCost));
    AverageUnitCost = NextAverageCost(OnHand, AverageUnitCost, quantity, unitCost);
    OnHand = checked(OnHand + quantity);
    Touch(utcNow);
    return new InventoryMovement(
        ProductId, InventoryMovementType.Receipt, quantity, unitCost, OnHand, AverageUnitCost, occurredOn,
        reason: null, DomainValidation.Optional(supplier, MaxSupplierLength), DomainValidation.Optional(reference, MaxReferenceLength),
        expenseEntryId, idempotencyKey, performedByUserId, utcNow);
  }

  /// <summary>
  /// A manual correction with its reason. Units come in or go out at the current average cost, which does not change.
  /// An adjustment never takes the stock below zero.
  /// </summary>
  public InventoryMovement Adjust(
      InventoryMovementType type, int quantity, string reason, DateOnly occurredOn, Guid idempotencyKey,
      Guid performedByUserId, DateTime utcNow)
  {
    if (type is not (InventoryMovementType.AdjustmentIn or InventoryMovementType.AdjustmentOut))
    {
      throw new ArgumentException("Only adjustment movements are allowed here.", nameof(type));
    }

    EnsureQuantity(quantity);
    var trimmedReason = DomainValidation.Required(reason?.Trim(), nameof(reason), MaxReasonLength, MinReasonLength);
    var next = type == InventoryMovementType.AdjustmentIn ? checked(OnHand + quantity) : checked(OnHand - quantity);
    if (next < 0)
    {
      throw new InvalidOperationException("An adjustment cannot leave the stock below zero.");
    }

    OnHand = next;
    Touch(utcNow);
    return new InventoryMovement(
        ProductId, type, quantity, AverageUnitCost, OnHand, AverageUnitCost, occurredOn, trimmedReason,
        supplier: null, reference: null, expenseEntryId: null, idempotencyKey, performedByUserId, utcNow);
  }

  /// <summary>
  /// ADR-0020 P10: the units of an approved bill leave at the current average cost, which does not change. The stock may
  /// end below zero (a bill can be closed without enough stock); the next receipt covers the shortfall first.
  /// </summary>
  public InventoryMovement Sell(
      int quantity, DateOnly occurredOn, Guid idempotencyKey, Guid performedByUserId, DateTime utcNow, string? reference = null)
  {
    EnsureQuantity(quantity);
    OnHand = checked(OnHand - quantity);
    Touch(utcNow);
    return new InventoryMovement(
        ProductId, InventoryMovementType.Sale, quantity, AverageUnitCost, OnHand, AverageUnitCost, occurredOn, reason: null,
        supplier: null, DomainValidation.Optional(reference, MaxReferenceLength), expenseEntryId: null, idempotencyKey, performedByUserId, utcNow);
  }

  /// <summary>
  /// ADR-0020 stage 4: the units of an unapproved bill come back at the cost they left with. They count in the weighted
  /// average like a receipt at that cost (covering any shortfall first, as a receipt does); without a cost (the product
  /// had none when it was sold) the average is left as it is.
  /// </summary>
  public InventoryMovement ReturnSale(
      int quantity, decimal? unitCost, DateOnly occurredOn, Guid idempotencyKey, Guid performedByUserId, DateTime utcNow, string? reference = null)
  {
    EnsureQuantity(quantity);
    if (unitCost is { } cost)
    {
      AverageUnitCost = OnHand <= 0 || AverageUnitCost is null
          ? cost
          : Math.Round(((OnHand * AverageUnitCost.Value) + (quantity * cost)) / (OnHand + quantity), 4, MidpointRounding.AwayFromZero);
    }

    OnHand = checked(OnHand + quantity);
    Touch(utcNow);
    return new InventoryMovement(
        ProductId, InventoryMovementType.SaleReversal, quantity, unitCost, OnHand, AverageUnitCost, occurredOn, reason: null,
        supplier: null, DomainValidation.Optional(reference, MaxReferenceLength), expenseEntryId: null, idempotencyKey, performedByUserId, utcNow);
  }

  public void SetMinStock(int? minStock, DateTime utcNow)
  {
    MinStock = minStock is { } value ? DomainValidation.EnsureRange(value, 0, MaxQuantity, nameof(minStock)) : null;
    Touch(utcNow);
  }

  /// <summary>Weighted average after receiving <paramref name="quantity"/> units at <paramref name="unitCost"/>, to 4 decimals.</summary>
  public static decimal NextAverageCost(int onHand, decimal? averageUnitCost, int quantity, int unitCost)
  {
    if (onHand <= 0 || averageUnitCost is null)
    {
      return unitCost;
    }

    var total = (onHand * averageUnitCost.Value) + ((decimal)quantity * unitCost);
    return Math.Round(total / (onHand + quantity), 4, MidpointRounding.AwayFromZero);
  }

  private static void EnsureQuantity(int quantity) => DomainValidation.EnsureRange(quantity, 1, MaxQuantity, nameof(quantity));

  private void Touch(DateTime utcNow) => UpdatedAt = DomainValidation.EnsureUtc(utcNow, nameof(utcNow));
}

/// <summary>
/// One row of a product's stock card (kardex). Append-only: the database rejects updates and deletes, and a correction
/// is a new movement. Each row keeps the stock and the average cost it left behind, so the card can be read on its own.
/// </summary>
public sealed class InventoryMovement
{
  private InventoryMovement()
  {
  }

  internal InventoryMovement(
      Guid productId, InventoryMovementType type, int quantity, decimal? unitCost, int onHandAfter,
      decimal? averageUnitCostAfter, DateOnly occurredOn, string? reason, string? supplier, string? reference,
      Guid? expenseEntryId, Guid idempotencyKey, Guid performedByUserId, DateTime createdAt)
  {
    if (idempotencyKey == Guid.Empty)
    {
      throw new ArgumentException("A movement needs an idempotency key.", nameof(idempotencyKey));
    }

    ProductId = productId;
    Type = type;
    Quantity = quantity;
    UnitCost = unitCost;
    OnHandAfter = onHandAfter;
    AverageUnitCostAfter = averageUnitCostAfter;
    OccurredOn = occurredOn;
    Reason = reason;
    Supplier = supplier;
    Reference = reference;
    ExpenseEntryId = expenseEntryId;
    IdempotencyKey = idempotencyKey;
    PerformedByUserId = performedByUserId;
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid ProductId { get; private set; }
  public InventoryMovementType Type { get; private set; }

  /// <summary>Always positive; the type gives the direction (<see cref="SignedQuantity"/>).</summary>
  public int Quantity { get; private set; }

  /// <summary>Receipt: the cost paid per unit. Adjustment: the average cost at that moment (null if there was none).</summary>
  public decimal? UnitCost { get; private set; }

  public int OnHandAfter { get; private set; }
  public decimal? AverageUnitCostAfter { get; private set; }

  /// <summary>Bogotá date the movement belongs to (a receipt may be recorded a few days late).</summary>
  public DateOnly OccurredOn { get; private set; }
  public string? Reason { get; private set; }
  public string? Supplier { get; private set; }
  public string? Reference { get; private set; }

  /// <summary>The purchase expense created with a receipt, when asked for (ADR-0020 Q13).</summary>
  public Guid? ExpenseEntryId { get; private set; }

  /// <summary>Sent by the client; a retried request returns the movement already stored.</summary>
  public Guid IdempotencyKey { get; private set; }
  public Guid PerformedByUserId { get; private set; }
  public DateTime CreatedAt { get; private set; }

  public int SignedQuantity => Type.IsInbound() ? Quantity : -Quantity;
}

public enum InventoryMovementType
{
  /// <summary>Merchandise received, with its unit cost.</summary>
  Receipt = 1,

  /// <summary>Manual correction that adds units.</summary>
  AdjustmentIn = 2,

  /// <summary>Manual correction that removes units.</summary>
  AdjustmentOut = 3,

  /// <summary>Reserved for the approval of a bill (ADR-0020 stage 3): units sold.</summary>
  Sale = 4,

  /// <summary>Reserved for the unapproval of a bill (ADR-0020 stage 4): a sale undone.</summary>
  SaleReversal = 5
}

public static class InventoryMovementTypeExtensions
{
  public static bool IsInbound(this InventoryMovementType type)
      => type is InventoryMovementType.Receipt or InventoryMovementType.AdjustmentIn or InventoryMovementType.SaleReversal;
}
