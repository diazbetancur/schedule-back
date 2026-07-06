using Barbershop.Domain.Finance;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

internal sealed class IncomeEntryConfiguration : IEntityTypeConfiguration<IncomeEntry>
{
  public void Configure(EntityTypeBuilder<IncomeEntry> builder)
  {
    builder.ToTable("income_entries", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint(
              "ck_income_entries_amount_non_negative",
              "\"Amount\" >= 0");
      tableBuilder.HasCheckConstraint(
              "ck_income_entries_base_price_non_negative",
              "\"BasePriceSnapshot\" >= 0");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.ServiceNameSnapshot).HasMaxLength(120).IsRequired();
    builder.Property(x => x.OccurredOn).HasColumnType("date");
    builder.Property(x => x.IsDeleted).HasDefaultValue(false);
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");

    builder.HasOne<Service>()
        .WithMany()
        .HasForeignKey(x => x.ServiceId)
        .OnDelete(DeleteBehavior.Restrict);

    builder.HasOne<StaffProfile>()
        .WithMany()
        .HasForeignKey(x => x.StaffProfileId)
        .OnDelete(DeleteBehavior.Restrict);

    builder.HasIndex(x => x.OccurredOn);
    builder.HasIndex(x => x.StaffProfileId);
  }
}
