using Barbershop.Domain.Finance;
using Barbershop.Domain.Products;
using Barbershop.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

/// <summary>ADR-0020 stage 1: the stock projection of each product.</summary>
internal sealed class ProductStockConfiguration : IEntityTypeConfiguration<ProductStock>
{
  public void Configure(EntityTypeBuilder<ProductStock> builder)
  {
    builder.ToTable("product_stock", table =>
    {
      table.HasCheckConstraint("ck_product_stock_min_stock_range", "\"MinStock\" IS NULL OR (\"MinStock\" >= 0 AND \"MinStock\" <= 100000)");
      table.HasCheckConstraint("ck_product_stock_average_cost_non_negative", "\"AverageUnitCost\" IS NULL OR \"AverageUnitCost\" >= 0");
    });

    builder.HasKey(x => x.ProductId);
    builder.Property(x => x.ProductId).ValueGeneratedNever();
    builder.Property(x => x.OnHand).IsRequired();
    builder.Property(x => x.AverageUnitCost).HasPrecision(18, 4);
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
    builder.Ignore(x => x.IsLow);

    builder.HasOne<Product>()
        .WithOne()
        .HasForeignKey<ProductStock>(x => x.ProductId)
        .OnDelete(DeleteBehavior.Restrict);
  }
}

/// <summary>ADR-0020 stage 1: the append-only stock card (kardex). Triggers in the migration reject UPDATE and DELETE.</summary>
internal sealed class InventoryMovementConfiguration : IEntityTypeConfiguration<InventoryMovement>
{
  public void Configure(EntityTypeBuilder<InventoryMovement> builder)
  {
    builder.ToTable("inventory_movements", table =>
    {
      table.HasCheckConstraint("ck_inventory_movements_quantity_positive", "\"Quantity\" > 0");
      table.HasCheckConstraint("ck_inventory_movements_unit_cost_non_negative", "\"UnitCost\" IS NULL OR \"UnitCost\" >= 0");
      // A receipt has a cost and no reason; an adjustment has a reason; only a receipt can carry the purchase expense.
      table.HasCheckConstraint(
          "ck_inventory_movements_shape",
          "(\"Type\" = 'Receipt' AND \"UnitCost\" IS NOT NULL AND \"Reason\" IS NULL) "
          + "OR (\"Type\" IN ('AdjustmentIn', 'AdjustmentOut') AND \"Reason\" IS NOT NULL AND \"ExpenseEntryId\" IS NULL) "
          + "OR (\"Type\" IN ('Sale', 'SaleReversal') AND \"ExpenseEntryId\" IS NULL)");
    });

    builder.HasKey(x => x.Id);
    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(24).IsRequired();
    builder.Property(x => x.Quantity).IsRequired();
    builder.Property(x => x.UnitCost).HasPrecision(18, 4);
    builder.Property(x => x.OnHandAfter).IsRequired();
    builder.Property(x => x.AverageUnitCostAfter).HasPrecision(18, 4);
    builder.Property(x => x.OccurredOn).IsRequired();
    builder.Property(x => x.Reason).HasMaxLength(ProductStock.MaxReasonLength);
    builder.Property(x => x.Supplier).HasMaxLength(ProductStock.MaxSupplierLength);
    builder.Property(x => x.Reference).HasMaxLength(ProductStock.MaxReferenceLength);
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Ignore(x => x.SignedQuantity);

    builder.HasOne<Product>()
        .WithMany()
        .HasForeignKey(x => x.ProductId)
        .OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<ExpenseEntry>()
        .WithMany()
        .HasForeignKey(x => x.ExpenseEntryId)
        .OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<User>()
        .WithMany()
        .HasForeignKey(x => x.PerformedByUserId)
        .OnDelete(DeleteBehavior.Restrict);

    builder.HasIndex(x => x.IdempotencyKey).IsUnique();
    builder.HasIndex(x => new { x.ProductId, x.CreatedAt });
    builder.HasIndex(x => x.ExpenseEntryId).IsUnique().HasFilter("\"ExpenseEntryId\" IS NOT NULL");
  }
}
