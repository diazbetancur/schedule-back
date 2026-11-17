using Barbershop.Domain.Appointments;
using Barbershop.Domain.Bills;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.Memberships;
using Barbershop.Domain.Products;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

/// <summary>ADR-0020 stage 2: the bill (internal receipt).</summary>
internal sealed class BillConfiguration : IEntityTypeConfiguration<Bill>
{
  public void Configure(EntityTypeBuilder<Bill> builder)
  {
    builder.ToTable("bills", table =>
    {
      table.HasCheckConstraint("ck_bills_amounts_non_negative",
          "\"Subtotal\" >= 0 AND \"DiscountTotal\" >= 0 AND \"TipTotal\" >= 0 AND \"DirectTipTotal\" >= 0 AND \"Total\" >= 0");
      table.HasCheckConstraint("ck_bills_total_consistent", "\"Total\" = \"Subtotal\" - \"DiscountTotal\" + \"TipTotal\"");
      // A number (and its series) exists from the close on, and only then; an open bill never spends one.
      table.HasCheckConstraint("ck_bills_number_shape",
          "(\"Number\" IS NULL AND \"Series\" IS NULL) OR (\"Number\" IS NOT NULL AND \"Series\" IS NOT NULL AND \"Number\" > 0)");
      table.HasCheckConstraint("ck_bills_closed_shape",
          "\"Status\" = 'Open' OR (\"Status\" = 'Voided' AND \"ClosedAt\" IS NULL) "
          + "OR (\"Number\" IS NOT NULL AND \"ClosedAt\" IS NOT NULL AND \"ClosedByUserId\" IS NOT NULL)");
      // P6: one payment method when something is charged, none otherwise.
      table.HasCheckConstraint("ck_bills_payment_method_shape",
          "\"ClosedAt\" IS NULL OR (\"Total\" > 0) = (\"PaymentMethodId\" IS NOT NULL)");
      table.HasCheckConstraint("ck_bills_approved_shape",
          "\"Status\" <> 'Approved' OR (\"CurrentApprovalId\" IS NOT NULL AND \"ApprovedAt\" IS NOT NULL AND \"ApprovedByUserId\" IS NOT NULL)");
      table.HasCheckConstraint("ck_bills_voided_shape",
          "(\"Status\" = 'Voided') = (\"VoidedAt\" IS NOT NULL AND \"VoidReason\" IS NOT NULL AND \"VoidedByUserId\" IS NOT NULL)");
    });

    builder.HasKey(x => x.Id);
    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
    builder.Property(x => x.Series).HasMaxLength(8);
    builder.Property(x => x.CustomerNameSnapshot).HasMaxLength(Bill.MaxCustomerNameLength).IsRequired();
    builder.Property(x => x.ServiceDate).HasColumnType("date");
    builder.Property(x => x.Notes).HasMaxLength(Bill.MaxNotesLength);
    builder.Property(x => x.PaymentMethodNameSnapshot).HasMaxLength(PaymentMethod.MaxNameLength);
    builder.Property(x => x.VoidReason).HasMaxLength(Bill.MaxReasonLength);
    builder.Property(x => x.OpenedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.ClosedAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.ApprovedAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.VoidedAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.IsSelfApproved).HasDefaultValue(false);
    builder.Property(x => x.Version).IsRowVersion();
    builder.Ignore(x => x.DisplayNumber);
    builder.Ignore(x => x.IsWalkIn);

    builder.HasMany(x => x.Lines)
        .WithOne()
        .HasForeignKey(x => x.BillId)
        .OnDelete(DeleteBehavior.Cascade);
    builder.Navigation(x => x.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field);

    builder.HasOne<Appointment>().WithMany().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<CustomerProfile>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<StaffProfile>().WithMany().HasForeignKey(x => x.StaffProfileId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<PaymentMethod>().WithMany().HasForeignKey(x => x.PaymentMethodId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<User>().WithMany().HasForeignKey(x => x.OpenedByUserId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<User>().WithMany().HasForeignKey(x => x.ClosedByUserId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<User>().WithMany().HasForeignKey(x => x.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<User>().WithMany().HasForeignKey(x => x.VoidedByUserId).OnDelete(DeleteBehavior.Restrict);

    builder.HasIndex(x => x.IdempotencyKey).IsUnique();
    builder.HasIndex(x => new { x.Series, x.Number }).IsUnique().HasFilter("\"Number\" IS NOT NULL");
    // One bill that is not voided per appointment: double billing an appointment is impossible.
    builder.HasIndex(x => x.AppointmentId)
        .IsUnique()
        .HasFilter("\"AppointmentId\" IS NOT NULL AND \"Status\" <> 'Voided'")
        .HasDatabaseName("IX_bills_AppointmentId_not_voided");
    builder.HasIndex(x => new { x.Status, x.OpenedAt });
    builder.HasIndex(x => new { x.StaffProfileId, x.Status });
    builder.HasIndex(x => x.CustomerId);
    builder.HasIndex(x => x.ServiceDate);
    // ADR-0020 stage 5: the cash closing reads by closing day; the history filters by payment method.
    builder.HasIndex(x => x.ClosedAt);
    builder.HasIndex(x => x.PaymentMethodId);
  }
}

internal sealed class BillLineConfiguration : IEntityTypeConfiguration<BillLine>
{
  public void Configure(EntityTypeBuilder<BillLine> builder)
  {
    builder.ToTable("bill_lines", table =>
    {
      table.HasCheckConstraint("ck_bill_lines_quantity_range", "\"Quantity\" >= 1 AND \"Quantity\" <= 1000");
      table.HasCheckConstraint("ck_bill_lines_unit_price_non_negative", "\"UnitPrice\" >= 0");
      table.HasCheckConstraint("ck_bill_lines_business_percentage_range",
          "\"BusinessPercentageSnapshot\" IS NULL OR (\"BusinessPercentageSnapshot\" >= 0 AND \"BusinessPercentageSnapshot\" <= 100)");
      // The shape of each type (like ck_service_records_operation_shape): only its own references, its own amount rule.
      table.HasCheckConstraint("ck_bill_lines_shape",
          "(\"Type\" = 'Service' AND \"StaffProfileId\" IS NOT NULL AND \"GeneralServiceId\" IS NOT NULL AND \"Quantity\" = 1 "
          + "AND \"ProductId\" IS NULL AND \"CustomerMembershipId\" IS NULL AND \"LoyaltyRedemptionId\" IS NULL "
          + "AND \"AppliesToLineId\" IS NULL AND \"TipPaymentMode\" IS NULL AND (\"IsCourtesy\" = false OR \"Reason\" IS NOT NULL) "
          + "AND \"Amount\" = CASE WHEN \"IsCourtesy\" THEN 0 ELSE \"UnitPrice\" END) "
          + "OR (\"Type\" = 'Product' AND \"ProductId\" IS NOT NULL AND \"ServiceId\" IS NULL AND \"CustomerMembershipId\" IS NULL "
          + "AND \"LoyaltyRedemptionId\" IS NULL AND \"AppliesToLineId\" IS NULL AND \"TipPaymentMode\" IS NULL AND \"IsCourtesy\" = false "
          + "AND \"Amount\" = \"Quantity\" * \"UnitPrice\") "
          + "OR (\"Type\" = 'MembershipConsumption' AND \"CustomerMembershipId\" IS NOT NULL AND \"ServiceId\" IS NOT NULL "
          + "AND \"StaffProfileId\" IS NOT NULL AND \"Quantity\" = 1 AND \"UnitPrice\" = 0 AND \"Amount\" = 0 AND \"ProductId\" IS NULL "
          + "AND \"LoyaltyRedemptionId\" IS NULL AND \"AppliesToLineId\" IS NULL AND \"TipPaymentMode\" IS NULL AND \"IsCourtesy\" = false) "
          + "OR (\"Type\" = 'Redemption' AND \"LoyaltyRedemptionId\" IS NOT NULL AND \"Quantity\" = 1 AND \"Amount\" = 0 "
          + "AND \"ProductId\" IS NULL AND \"CustomerMembershipId\" IS NULL AND \"AppliesToLineId\" IS NULL "
          + "AND \"TipPaymentMode\" IS NULL AND \"IsCourtesy\" = false) "
          + "OR (\"Type\" = 'Discount' AND \"Reason\" IS NOT NULL AND \"UnitPrice\" > 0 AND \"Amount\" = -\"UnitPrice\" AND \"Quantity\" = 1 "
          + "AND \"StaffProfileId\" IS NULL AND \"ServiceId\" IS NULL AND \"ProductId\" IS NULL AND \"CustomerMembershipId\" IS NULL "
          + "AND \"LoyaltyRedemptionId\" IS NULL AND \"TipPaymentMode\" IS NULL AND \"IsCourtesy\" = false) "
          + "OR (\"Type\" = 'Tip' AND \"StaffProfileId\" IS NOT NULL AND \"TipPaymentMode\" IS NOT NULL AND \"UnitPrice\" > 0 "
          + "AND \"Quantity\" = 1 AND \"Amount\" = CASE WHEN \"TipPaymentMode\" = 'WithBill' THEN \"UnitPrice\" ELSE 0 END "
          + "AND \"ServiceId\" IS NULL AND \"ProductId\" IS NULL AND \"CustomerMembershipId\" IS NULL AND \"LoyaltyRedemptionId\" IS NULL "
          + "AND \"AppliesToLineId\" IS NULL AND \"IsCourtesy\" = false)");
    });

    builder.HasKey(x => x.Id);
    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(24).IsRequired();
    builder.Property(x => x.NameSnapshot).HasMaxLength(BillLine.MaxNameLength).IsRequired();
    builder.Property(x => x.Reason).HasMaxLength(Bill.MaxReasonLength);
    builder.Property(x => x.TipPaymentMode).HasConversion<string>().HasMaxLength(16);
    builder.Property(x => x.IsCourtesy).HasDefaultValue(false);
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
    builder.Ignore(x => x.IsChargeable);
    builder.Ignore(x => x.GrossAmount);

    builder.HasOne<StaffProfile>().WithMany().HasForeignKey(x => x.StaffProfileId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<GeneralService>().WithMany().HasForeignKey(x => x.GeneralServiceId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<CustomerMembership>().WithMany().HasForeignKey(x => x.CustomerMembershipId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<LoyaltyRedemption>().WithMany().HasForeignKey(x => x.LoyaltyRedemptionId).OnDelete(DeleteBehavior.Restrict);

    builder.HasIndex(x => new { x.BillId, x.Position });
    builder.HasIndex(x => x.LoyaltyRedemptionId);
    builder.HasIndex(x => x.ProductId);
  }
}

/// <summary>Append-only audit trail; triggers in the migration reject UPDATE and DELETE.</summary>
internal sealed class BillEventConfiguration : IEntityTypeConfiguration<BillEvent>
{
  public void Configure(EntityTypeBuilder<BillEvent> builder)
  {
    builder.ToTable("bill_events");
    builder.HasKey(x => x.Id);
    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(32).IsRequired();
    builder.Property(x => x.StatusAtEvent).HasConversion<string>().HasMaxLength(16).IsRequired();
    builder.Property(x => x.Reason).HasMaxLength(Bill.MaxReasonLength);
    builder.Property(x => x.BeforeJson).HasColumnType("jsonb");
    builder.Property(x => x.AfterJson).HasColumnType("jsonb");
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();

    builder.HasOne<Bill>().WithMany().HasForeignKey(x => x.BillId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    builder.HasIndex(x => new { x.BillId, x.CreatedAt });
  }
}

internal sealed class BillApprovalConfiguration : IEntityTypeConfiguration<BillApproval>
{
  public void Configure(EntityTypeBuilder<BillApproval> builder)
  {
    builder.ToTable("bill_approvals", table =>
    {
      table.HasCheckConstraint("ck_bill_approvals_sequence_positive", "\"Sequence\" >= 1");
      // Stage 4: an unapproval names who, when, why and its idempotency key, or none of them.
      table.HasCheckConstraint("ck_bill_approvals_unapproval_shape",
          "(\"UnapprovedAt\" IS NULL AND \"UnapprovedByUserId\" IS NULL AND \"UnapprovalReason\" IS NULL AND \"UnapprovalKey\" IS NULL) "
          + "OR (\"UnapprovedAt\" IS NOT NULL AND \"UnapprovedByUserId\" IS NOT NULL AND \"UnapprovalReason\" IS NOT NULL AND \"UnapprovalKey\" IS NOT NULL)");
    });
    builder.HasKey(x => x.Id);
    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.LinesJson).HasColumnType("jsonb").IsRequired();
    builder.Property(x => x.Notes).HasMaxLength(Bill.MaxNotesLength);
    builder.Property(x => x.ApprovedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UnapprovedAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.UnapprovalReason).HasMaxLength(Bill.MaxReasonLength);
    builder.Property(x => x.IsSelfUnapproved).HasDefaultValue(false);
    builder.Ignore(x => x.IsUnapproved);

    builder.HasOne<Bill>().WithMany().HasForeignKey(x => x.BillId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<User>().WithMany().HasForeignKey(x => x.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<User>().WithMany().HasForeignKey(x => x.UnapprovedByUserId).OnDelete(DeleteBehavior.Restrict);
    builder.HasIndex(x => x.UnapprovalKey).IsUnique().HasFilter("\"UnapprovalKey\" IS NOT NULL");
    builder.HasOne<CustomerProfile>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
    builder.HasIndex(x => x.IdempotencyKey).IsUnique();
    builder.HasIndex(x => new { x.BillId, x.Sequence }).IsUnique();
  }
}

internal sealed class BillPostingConfiguration : IEntityTypeConfiguration<BillPosting>
{
  public void Configure(EntityTypeBuilder<BillPosting> builder)
  {
    builder.ToTable("bill_postings", table => table.HasCheckConstraint("ck_bill_postings_target_shape",
        "(\"Effect\" = 'Income' AND \"IncomeEntryId\" IS NOT NULL) "
        + "OR (\"Effect\" = 'MembershipConsumption' AND \"MembershipConsumptionId\" IS NOT NULL) "
        + "OR (\"Effect\" = 'LoyaltyAccrual' AND \"LoyaltyLotId\" IS NOT NULL) "
        + "OR (\"Effect\" = 'RedemptionDelivery' AND \"LoyaltyRedemptionId\" IS NOT NULL) "
        + "OR (\"Effect\" = 'InventorySale' AND \"InventoryMovementId\" IS NOT NULL)"));
    builder.HasKey(x => x.Id);
    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Effect).HasConversion<string>().HasMaxLength(32).IsRequired();
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.ReversedAt).HasColumnType("timestamp with time zone");
    builder.Ignore(x => x.IsReversed);

    builder.HasOne<BillApproval>().WithMany().HasForeignKey(x => x.ApprovalId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<BillLine>().WithMany().HasForeignKey(x => x.BillLineId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<IncomeEntry>().WithMany().HasForeignKey(x => x.IncomeEntryId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<MembershipConsumption>().WithMany().HasForeignKey(x => x.MembershipConsumptionId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<LoyaltyLot>().WithMany().HasForeignKey(x => x.LoyaltyLotId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<LoyaltyRedemption>().WithMany().HasForeignKey(x => x.LoyaltyRedemptionId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<InventoryMovement>().WithMany().HasForeignKey(x => x.InventoryMovementId).OnDelete(DeleteBehavior.Restrict);

    builder.HasIndex(x => new { x.ApprovalId, x.BillLineId, x.Effect }).IsUnique();
    builder.HasIndex(x => x.IncomeEntryId).IsUnique().HasFilter("\"IncomeEntryId\" IS NOT NULL");
    builder.HasIndex(x => x.MembershipConsumptionId).IsUnique().HasFilter("\"MembershipConsumptionId\" IS NOT NULL");
    builder.HasIndex(x => x.LoyaltyLotId).IsUnique().HasFilter("\"LoyaltyLotId\" IS NOT NULL");
    builder.HasIndex(x => x.InventoryMovementId).IsUnique().HasFilter("\"InventoryMovementId\" IS NOT NULL");
    builder.HasIndex(x => x.LoyaltyRedemptionId);
  }
}

internal sealed class PaymentMethodConfiguration : IEntityTypeConfiguration<PaymentMethod>
{
  /// <summary>Q1: the methods the assistant recommended and the user accepted; the administrator renames or deactivates them.</summary>
  internal static readonly (Guid Id, string Name)[] Defaults =
  [
    (new Guid("5d0c3a0e-6a4b-4f5e-9b0a-1c2d3e4f5a01"), "Efectivo"),
    (new Guid("5d0c3a0e-6a4b-4f5e-9b0a-1c2d3e4f5a02"), "Transferencia"),
    (new Guid("5d0c3a0e-6a4b-4f5e-9b0a-1c2d3e4f5a03"), "Tarjeta"),
    (new Guid("5d0c3a0e-6a4b-4f5e-9b0a-1c2d3e4f5a04"), "Nequi o Daviplata"),
    (new Guid("5d0c3a0e-6a4b-4f5e-9b0a-1c2d3e4f5a05"), "Otro"),
  ];

  public void Configure(EntityTypeBuilder<PaymentMethod> builder)
  {
    builder.ToTable("payment_methods");
    builder.HasKey(x => x.Id);
    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Name).HasMaxLength(PaymentMethod.MaxNameLength).IsRequired();
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
    builder.HasIndex(x => x.Name).IsUnique();

    var createdAt = new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);
    builder.HasData(Defaults.Select((method, index) => new
    {
      method.Id,
      method.Name,
      SortOrder = (index + 1) * 10,
      IsActive = true,
      CreatedAt = createdAt,
      UpdatedAt = (DateTime?)null,
    }));
  }
}

internal sealed class BillNumberSeriesConfiguration : IEntityTypeConfiguration<BillNumberSeries>
{
  public void Configure(EntityTypeBuilder<BillNumberSeries> builder)
  {
    builder.ToTable("bill_number_series", table => table.HasCheckConstraint("ck_bill_number_series_next_positive", "\"NextNumber\" >= 1"));
    builder.HasKey(x => x.Prefix);
    builder.Property(x => x.Prefix).HasMaxLength(8);
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
    builder.HasData(new { Prefix = BillNumberSeries.DefaultPrefix, NextNumber = 1, UpdatedAt = (DateTime?)null });
  }
}
