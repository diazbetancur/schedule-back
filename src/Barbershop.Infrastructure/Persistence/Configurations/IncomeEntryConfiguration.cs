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
      tableBuilder.HasCheckConstraint(
              "ck_income_entries_business_percentage_range",
              "\"BusinessPercentageSnapshot\" >= 0 AND \"BusinessPercentageSnapshot\" <= 100");
      // A service income names its service and professional; a membership sale has neither and is
      // 100% business (so it can never produce a professional share). Every compared column is NOT NULL
      // except the three checked with IS [NOT] NULL, so the expression never evaluates to NULL.
      tableBuilder.HasCheckConstraint(
              "ck_income_entries_kind_consistency",
              "(\"Kind\" = 'Service' AND \"ServiceId\" IS NOT NULL AND \"StaffProfileId\" IS NOT NULL AND \"ServiceNameSnapshot\" IS NOT NULL) "
              + "OR (\"Kind\" = 'MembershipSale' AND \"ServiceId\" IS NULL AND \"StaffProfileId\" IS NULL AND \"ServiceNameSnapshot\" IS NULL "
              + "AND \"BusinessPercentageSnapshot\" = 100 AND \"BasePriceSnapshot\" = 0 AND \"IsPromo\" = false)");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    // Existing rows predate membership sales: the migration's default makes them all service income.
    builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(24).IsRequired().HasDefaultValue(IncomeKind.Service);
    builder.Property(x => x.ServiceNameSnapshot).HasMaxLength(120);
    builder.Property(x => x.BusinessPercentageSnapshot).IsRequired().HasDefaultValue(0);
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
