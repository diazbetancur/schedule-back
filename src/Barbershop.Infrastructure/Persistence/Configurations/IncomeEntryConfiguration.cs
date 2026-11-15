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
      // ADR-0020 Q8: only a counter-entry (it names the income it reverses) carries a negative amount.
      tableBuilder.HasCheckConstraint(
              "ck_income_entries_amount_non_negative",
              "\"Amount\" >= 0 OR \"ReversesIncomeEntryId\" IS NOT NULL");
      tableBuilder.HasCheckConstraint(
              "ck_income_entries_base_price_non_negative",
              "\"BasePriceSnapshot\" >= 0");
      tableBuilder.HasCheckConstraint(
              "ck_income_entries_business_percentage_range",
              "\"BusinessPercentageSnapshot\" >= 0 AND \"BusinessPercentageSnapshot\" <= 100");
      // A service income names its service and professional; a membership sale has neither and is
      // 100% business (so it can never produce a professional share). ADR-0020 stage 3 adds the product sale (100%
      // business, with its product and units, always from a bill) and the tip (100% of the professional, always from a
      // bill). Every row stored before satisfies the first two branches, which are unchanged. Every compared column is
      // NOT NULL except the ones checked with IS [NOT] NULL, so the expression never evaluates to NULL.
      tableBuilder.HasCheckConstraint(
              "ck_income_entries_kind_consistency",
              "(\"Kind\" = 'Service' AND \"ServiceId\" IS NOT NULL AND \"StaffProfileId\" IS NOT NULL AND \"ServiceNameSnapshot\" IS NOT NULL) "
              + "OR (\"Kind\" = 'MembershipSale' AND \"ServiceId\" IS NULL AND \"StaffProfileId\" IS NULL AND \"ServiceNameSnapshot\" IS NULL "
              + "AND \"BusinessPercentageSnapshot\" = 100 AND \"BasePriceSnapshot\" = 0 AND \"IsPromo\" = false) "
              + "OR (\"Kind\" = 'ProductSale' AND \"ServiceId\" IS NULL AND \"StaffProfileId\" IS NULL AND \"ServiceNameSnapshot\" IS NULL "
              + "AND \"ProductId\" IS NOT NULL AND \"ProductNameSnapshot\" IS NOT NULL AND \"Quantity\" IS NOT NULL AND \"Quantity\" > 0 "
              + "AND \"BillLineId\" IS NOT NULL AND \"BusinessPercentageSnapshot\" = 100 AND \"IsPromo\" = false) "
              + "OR (\"Kind\" = 'Tip' AND \"ServiceId\" IS NULL AND \"StaffProfileId\" IS NOT NULL AND \"ServiceNameSnapshot\" IS NULL "
              + "AND \"ProductId\" IS NULL AND \"BillLineId\" IS NOT NULL AND \"BusinessPercentageSnapshot\" = 0 "
              + "AND \"BasePriceSnapshot\" = 0 AND \"IsPromo\" = false)");
      // P7: the commission base is the value before discounts, never below what was charged.
      // The commission base never goes below what was charged; a counter-entry mirrors it with both signs negated.
      tableBuilder.HasCheckConstraint(
              "ck_income_entries_commission_base_range",
              "\"CommissionBaseAmount\" IS NULL "
              + "OR (\"ReversesIncomeEntryId\" IS NULL AND \"CommissionBaseAmount\" >= \"Amount\") "
              + "OR (\"ReversesIncomeEntryId\" IS NOT NULL AND \"CommissionBaseAmount\" <= \"Amount\")");
      // A counter-entry reverses an income posted by a bill, and never adds money.
      tableBuilder.HasCheckConstraint(
              "ck_income_entries_reversal_shape",
              "\"ReversesIncomeEntryId\" IS NULL OR (\"BillLineId\" IS NOT NULL AND \"Amount\" <= 0)");
      tableBuilder.HasCheckConstraint(
              "ck_income_entries_bill_shape",
              "(\"BillId\" IS NULL) = (\"BillLineId\" IS NULL)");
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

    builder.Property(x => x.ProductNameSnapshot).HasMaxLength(120);
    builder.Property(x => x.UnitCostSnapshot).HasPrecision(18, 4);
    builder.Ignore(x => x.BusinessAmount);
    builder.Ignore(x => x.IsReversal);
    builder.HasOne<IncomeEntry>()
        .WithMany()
        .HasForeignKey(x => x.ReversesIncomeEntryId)
        .OnDelete(DeleteBehavior.Restrict);
    // An income is reversed at most once.
    builder.HasIndex(x => x.ReversesIncomeEntryId).IsUnique().HasFilter("\"ReversesIncomeEntryId\" IS NOT NULL");
    builder.Ignore(x => x.ProfessionalAmount);

    builder.HasOne<global::Barbershop.Domain.Products.Product>()
        .WithMany()
        .HasForeignKey(x => x.ProductId)
        .OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<global::Barbershop.Domain.Bills.Bill>()
        .WithMany()
        .HasForeignKey(x => x.BillId)
        .OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<global::Barbershop.Domain.Bills.BillLine>()
        .WithMany()
        .HasForeignKey(x => x.BillLineId)
        .OnDelete(DeleteBehavior.Restrict);

    builder.HasIndex(x => x.BillId);
    builder.HasIndex(x => x.BillLineId);
    builder.HasIndex(x => x.OccurredOn);
    builder.HasIndex(x => x.StaffProfileId);
  }
}
