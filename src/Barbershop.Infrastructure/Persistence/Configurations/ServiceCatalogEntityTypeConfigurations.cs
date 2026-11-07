using Barbershop.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

/// <summary>Names of the unique indexes that turn a duplicated catalog name into a controlled conflict.</summary>
internal static class ServiceCatalogConstraints
{
  public const string CategoryNameUnique = "ux_service_categories_normalized_name_live";
  public const string GeneralServiceNameUnique = "ux_general_services_category_normalized_name_live";
}

internal sealed class ServiceCategoryConfiguration : IEntityTypeConfiguration<ServiceCategory>
{
  public void Configure(EntityTypeBuilder<ServiceCategory> builder)
  {
    builder.ToTable("service_categories", table =>
    {
      table.HasCheckConstraint("ck_service_categories_sort_order_range", "\"SortOrder\" >= 0 AND \"SortOrder\" <= 9999");
      table.HasCheckConstraint("ck_service_categories_public_code_format", "\"PublicCode\" ~ '^[a-z0-9]{4,16}$'");
    });

    builder.HasKey(x => x.Id);
    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Name).HasMaxLength(ServiceCategory.NameMaxLength).IsRequired();
    builder.Property(x => x.NormalizedName).HasMaxLength(ServiceCategory.NameMaxLength).IsRequired();
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
        .HasDatabaseName(ServiceCatalogConstraints.CategoryNameUnique);
    builder.HasIndex(x => x.PublicCode).IsUnique();

    builder.HasData(new
    {
      Id = ServiceCatalogDefaults.BarberiaCategoryId,
      Name = ServiceCatalogDefaults.BarberiaCategoryName,
      NormalizedName = ServiceCatalogCode.NormalizeName(ServiceCatalogDefaults.BarberiaCategoryName),
      SortOrder = 0,
      PublicCode = ServiceCatalogDefaults.BarberiaCategoryCode,
      IsActive = true,
      IsDeleted = false,
      CreatedAt = ServiceCatalogDefaults.SeededAt,
    });
  }
}

internal sealed class GeneralServiceConfiguration : IEntityTypeConfiguration<GeneralService>
{
  public void Configure(EntityTypeBuilder<GeneralService> builder)
  {
    builder.ToTable("general_services", table =>
    {
      table.HasCheckConstraint("ck_general_services_sort_order_range", "\"SortOrder\" >= 0 AND \"SortOrder\" <= 9999");
      table.HasCheckConstraint("ck_general_services_public_code_format", "\"PublicCode\" ~ '^[a-z0-9]{4,16}$'");
    });

    builder.HasKey(x => x.Id);
    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Name).HasMaxLength(GeneralService.NameMaxLength).IsRequired();
    builder.Property(x => x.NormalizedName).HasMaxLength(GeneralService.NameMaxLength).IsRequired();
    builder.Property(x => x.Description).HasMaxLength(GeneralService.DescriptionMaxLength);
    builder.Property(x => x.SortOrder).IsRequired();
    builder.Property(x => x.PublicCode).HasMaxLength(ServiceCatalogCode.MaxLength).IsRequired();
    builder.Property(x => x.IsActive).IsRequired();
    builder.Property(x => x.IsDeleted).IsRequired();
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");

    builder.HasOne<ServiceCategory>()
        .WithMany()
        .HasForeignKey(x => x.CategoryId)
        .OnDelete(DeleteBehavior.Restrict);

    // Equivalent names are unique within a category (not across categories), enforced by PostgreSQL.
    builder.HasIndex(x => new { x.CategoryId, x.NormalizedName })
        .IsUnique()
        .HasFilter("\"IsDeleted\" = false")
        .HasDatabaseName(ServiceCatalogConstraints.GeneralServiceNameUnique);
    builder.HasIndex(x => x.PublicCode).IsUnique();

    builder.HasData(new
    {
      Id = ServiceCatalogDefaults.CorteGeneralServiceId,
      CategoryId = ServiceCatalogDefaults.BarberiaCategoryId,
      Name = ServiceCatalogDefaults.CorteGeneralServiceName,
      NormalizedName = ServiceCatalogCode.NormalizeName(ServiceCatalogDefaults.CorteGeneralServiceName),
      SortOrder = 0,
      PublicCode = ServiceCatalogDefaults.CorteGeneralServiceCode,
      IsActive = true,
      IsDeleted = false,
      CreatedAt = ServiceCatalogDefaults.SeededAt,
    });
  }
}
