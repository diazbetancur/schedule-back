using Barbershop.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

internal sealed class ServiceConfiguration : IEntityTypeConfiguration<Service>
{
  public void Configure(EntityTypeBuilder<Service> builder)
  {
    builder.ToTable("services", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint(
              "ck_services_base_price_non_negative",
              "\"BasePrice\" >= 0");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
    builder.Property(x => x.BasePrice).IsRequired();
    builder.Property(x => x.IsActive).HasDefaultValue(true);
    builder.Property(x => x.IsDeleted).HasDefaultValue(false);
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");

    // Backstop: prevents exact-name duplicates among live services at the DB level.
    // Case-insensitive uniqueness is enforced in the service layer (Task 2).
    builder.HasIndex(x => x.Name)
        .IsUnique()
        .HasFilter("\"IsDeleted\" = false");
  }
}
