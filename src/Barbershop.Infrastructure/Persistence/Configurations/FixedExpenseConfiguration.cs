using Barbershop.Domain.Finance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

internal sealed class FixedExpenseConfiguration : IEntityTypeConfiguration<FixedExpense>
{
  public void Configure(EntityTypeBuilder<FixedExpense> builder)
  {
    builder.ToTable("fixed_expenses", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint(
              "ck_fixed_expenses_default_amount_non_negative",
              "\"DefaultAmount\" IS NULL OR \"DefaultAmount\" >= 0");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
    builder.Property(x => x.IsActive).HasDefaultValue(true);
    builder.Property(x => x.IsDeleted).HasDefaultValue(false);
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");

    builder.HasIndex(x => x.Name)
        .IsUnique()
        .HasFilter("\"IsDeleted\" = false");
  }
}
