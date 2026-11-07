using Barbershop.Domain.Media;
using Barbershop.Domain.Products;
using Barbershop.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

/// <summary>Names of the unique indexes that turn a duplicated product catalog name into a controlled conflict.</summary>
internal static class ProductCatalogConstraints
{
  public const string CategoryNameUnique = "ux_product_categories_normalized_name_live";
  public const string ProductNameUnique = "ux_products_category_normalized_name_live";
}

internal sealed class ProductCategoryConfiguration : IEntityTypeConfiguration<ProductCategory>
{
  public void Configure(EntityTypeBuilder<ProductCategory> builder)
  {
    builder.ToTable("product_categories", table =>
    {
      table.HasCheckConstraint("ck_product_categories_sort_order_range", "\"SortOrder\" >= 0 AND \"SortOrder\" <= 9999");
      table.HasCheckConstraint("ck_product_categories_public_code_format", "\"PublicCode\" ~ '^[a-z0-9]{4,16}$'");
    });

    builder.HasKey(x => x.Id);
    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Name).HasMaxLength(ProductCategory.NameMaxLength).IsRequired();
    builder.Property(x => x.NormalizedName).HasMaxLength(ProductCategory.NameMaxLength).IsRequired();
    builder.Property(x => x.SortOrder).IsRequired();
    builder.Property(x => x.PublicCode).HasMaxLength(ServiceCatalogCode.MaxLength).IsRequired();
    builder.Property(x => x.IsActive).IsRequired();
    builder.Property(x => x.IsDeleted).IsRequired();
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");

    // Equivalent names (case and outer spaces ignored) are unique among live categories, enforced by PostgreSQL so
    // simultaneous requests end with one category and a conflict for the other.
    builder.HasIndex(x => x.NormalizedName)
        .IsUnique()
        .HasFilter("\"IsDeleted\" = false")
        .HasDatabaseName(ProductCatalogConstraints.CategoryNameUnique);
    builder.HasIndex(x => x.PublicCode).IsUnique();
  }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
  public void Configure(EntityTypeBuilder<Product> builder)
  {
    builder.ToTable("products", table =>
    {
      table.HasCheckConstraint("ck_products_sort_order_range", "\"SortOrder\" >= 0 AND \"SortOrder\" <= 9999");
      table.HasCheckConstraint("ck_products_public_code_format", "\"PublicCode\" ~ '^[a-z0-9]{4,16}$'");
      table.HasCheckConstraint("ck_products_price_non_negative", "\"Price\" >= 0");
    });

    builder.HasKey(x => x.Id);
    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Name).HasMaxLength(Product.NameMaxLength).IsRequired();
    builder.Property(x => x.NormalizedName).HasMaxLength(Product.NameMaxLength).IsRequired();
    builder.Property(x => x.Description).HasMaxLength(Product.DescriptionMaxLength);
    builder.Property(x => x.Price).IsRequired();
    builder.Property(x => x.SortOrder).IsRequired();
    builder.Property(x => x.PublicCode).HasMaxLength(ServiceCatalogCode.MaxLength).IsRequired();
    builder.Property(x => x.IsActive).IsRequired();
    builder.Property(x => x.IsDeleted).IsRequired();
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");

    builder.HasOne<ProductCategory>()
        .WithMany()
        .HasForeignKey(x => x.CategoryId)
        .OnDelete(DeleteBehavior.Restrict);

    // Same as the other image references (staff photo, banners): media rows are only archived, never deleted, and if
    // one ever were, the product simply loses its picture instead of blocking the cleanup.
    builder.HasOne<MediaAsset>()
        .WithMany()
        .HasForeignKey(x => x.ImageMediaAssetId)
        .OnDelete(DeleteBehavior.SetNull);

    // Equivalent names are unique within a category (not across categories), enforced by PostgreSQL.
    builder.HasIndex(x => new { x.CategoryId, x.NormalizedName })
        .IsUnique()
        .HasFilter("\"IsDeleted\" = false")
        .HasDatabaseName(ProductCatalogConstraints.ProductNameUnique);
    builder.HasIndex(x => x.PublicCode).IsUnique();
  }
}
