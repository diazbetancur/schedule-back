using Barbershop.Domain.Customers;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

internal sealed class LoyaltyLotConfiguration : IEntityTypeConfiguration<LoyaltyLot>
{
  public void Configure(EntityTypeBuilder<LoyaltyLot> builder)
  {
    builder.ToTable("loyalty_lots", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint("ck_loyalty_lots_initial_positive", "\"InitialPoints\" > 0");
      tableBuilder.HasCheckConstraint("ck_loyalty_lots_remaining_range", "\"RemainingPoints\" >= 0 AND \"RemainingPoints\" <= \"InitialPoints\"");
      // Reserved points left the free balance; together they never exceed what the lot opened with.
      tableBuilder.HasCheckConstraint(
          "ck_loyalty_lots_reserved_range",
          "\"ReservedPoints\" >= 0 AND \"RemainingPoints\" + \"ReservedPoints\" <= \"InitialPoints\"");
      tableBuilder.HasCheckConstraint("ck_loyalty_lots_expiry_after_accrual", "\"ExpiresOn\" >= \"AccruedOn\"");
      // A lot created by a merge names both the lot it came from and the profile that earned the points.
      tableBuilder.HasCheckConstraint(
          "ck_loyalty_lots_origin_shape",
          "(\"OriginLotId\" IS NULL AND \"OriginCustomerId\" IS NULL) OR (\"OriginLotId\" IS NOT NULL AND \"OriginCustomerId\" IS NOT NULL)");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.SourceType).HasConversion<string>().HasMaxLength(24).IsRequired();
    builder.Property(x => x.ReservedPoints).IsRequired();
    builder.Property(x => x.AccruedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.AccruedOn).HasColumnType("date");
    builder.Property(x => x.ExpiresOn).HasColumnType("date");
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.Version).IsRowVersion();

    // One original accrual per source. Lots opened by merges keep the same source and coexist with it.
    builder.HasIndex(x => new { x.SourceType, x.SourceId })
        .IsUnique()
        .HasFilter("\"OriginLotId\" IS NULL")
        .HasDatabaseName("IX_loyalty_lots_original_source");
    builder.HasIndex(x => new { x.SourceType, x.SourceId });

    // A lot is moved by a merge at most once.
    builder.HasIndex(x => x.OriginLotId).IsUnique().HasFilter("\"OriginLotId\" IS NOT NULL");
    builder.HasIndex(x => new { x.CustomerId, x.ExpiresOn });

    builder.HasOne<CustomerProfile>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<CustomerProfile>().WithMany().HasForeignKey(x => x.OriginCustomerId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<LoyaltyLot>().WithMany().HasForeignKey(x => x.OriginLotId).OnDelete(DeleteBehavior.Restrict);
  }
}

internal sealed class LoyaltyDebtConfiguration : IEntityTypeConfiguration<LoyaltyDebt>
{
  public void Configure(EntityTypeBuilder<LoyaltyDebt> builder)
  {
    builder.ToTable("loyalty_debts", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint("ck_loyalty_debts_amount_positive", "\"Amount\" > 0");
      tableBuilder.HasCheckConstraint("ck_loyalty_debts_outstanding_range", "\"OutstandingPoints\" >= 0 AND \"OutstandingPoints\" <= \"Amount\"");
      // A moved debt may be older than its row, never newer.
      tableBuilder.HasCheckConstraint("ck_loyalty_debts_incurred_not_after_created", "\"IncurredAt\" <= \"CreatedAt\"");
      tableBuilder.HasCheckConstraint(
          "ck_loyalty_debts_origin_shape",
          "(\"Origin\" = 'MergeTransfer' AND \"OriginDebtId\" IS NOT NULL AND \"OriginCustomerId\" IS NOT NULL) "
          + "OR (\"Origin\" <> 'MergeTransfer' AND \"OriginDebtId\" IS NULL)");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Origin).HasConversion<string>().HasMaxLength(24).IsRequired();
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.IncurredAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.Version).IsRowVersion();

    // A debt is moved by a merge at most once.
    builder.HasIndex(x => x.OriginDebtId).IsUnique().HasFilter("\"OriginDebtId\" IS NOT NULL");
    builder.HasIndex(x => new { x.CustomerId, x.IncurredAt });

    builder.HasOne<CustomerProfile>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<CustomerProfile>().WithMany().HasForeignKey(x => x.OriginCustomerId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<LoyaltyDebt>().WithMany().HasForeignKey(x => x.OriginDebtId).OnDelete(DeleteBehavior.Restrict);
  }
}

internal sealed class LoyaltyAdjustmentConfiguration : IEntityTypeConfiguration<LoyaltyAdjustment>
{
  public void Configure(EntityTypeBuilder<LoyaltyAdjustment> builder)
  {
    builder.ToTable("loyalty_adjustments", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint("ck_loyalty_adjustments_points_positive", "\"Points\" > 0");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Direction).HasConversion<string>().HasMaxLength(16).IsRequired();
    builder.Property(x => x.Reason).HasMaxLength(LoyaltyAdjustment.MaxReasonLength).IsRequired();
    builder.Property(x => x.PerformedAt).HasColumnType("timestamp with time zone").IsRequired();

    builder.HasIndex(x => x.IdempotencyKey).IsUnique();
    builder.HasIndex(x => new { x.CustomerId, x.PerformedAt });

    builder.HasOne<CustomerProfile>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<User>().WithMany().HasForeignKey(x => x.PerformedByUserId).OnDelete(DeleteBehavior.Restrict);
  }
}

internal sealed class LoyaltyMovementConfiguration : IEntityTypeConfiguration<LoyaltyMovement>
{
  public void Configure(EntityTypeBuilder<LoyaltyMovement> builder)
  {
    builder.ToTable("loyalty_movements", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint("ck_loyalty_movements_points_positive", "\"Points\" > 0");
      tableBuilder.HasCheckConstraint("ck_loyalty_movements_target", "\"LotId\" IS NOT NULL OR \"DebtId\" IS NOT NULL");
      // Holds, releases and consumptions always name their redemption and lot.
      tableBuilder.HasCheckConstraint(
          "ck_loyalty_movements_redemption_shape",
          "\"Type\" NOT IN ('RedemptionHold', 'RedemptionRelease', 'RedemptionConsumed') "
          + "OR (\"RedemptionId\" IS NOT NULL AND \"LotId\" IS NOT NULL)");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(32).IsRequired();
    builder.Property(x => x.Reason).HasMaxLength(LoyaltyAdjustment.MaxReasonLength);
    builder.Property(x => x.PerformedAt).HasColumnType("timestamp with time zone").IsRequired();

    // A lot is opened, expired, withdrawn by an annulment and moved by a merge at most once each; a debt settles
    // against a given lot at most once (afterwards one of them is exhausted). Retries can never repeat them.
    // Movements tied to a redemption are excluded here: points a redemption releases from an already expired (or
    // annulled) lot expire (or are withdrawn) again in the same transaction, after the lot's own expiration
    // (ADR-0015). Those are unique per lot, redemption and type instead, so a release never happens twice.
    builder.HasIndex(x => new { x.LotId, x.Type })
        .IsUnique()
        .HasFilter("\"Type\" IN ('Accrual', 'MergeTransferIn', 'AdjustmentCredit', 'Expiration', 'MembershipAnnulmentWithdrawal', 'MergeTransferOut') "
            + "AND \"RedemptionId\" IS NULL")
        .HasDatabaseName("IX_loyalty_movements_once_per_lot");
    builder.HasIndex(x => new { x.LotId, x.RedemptionId, x.Type })
        .IsUnique()
        .HasFilter("\"RedemptionId\" IS NOT NULL")
        .HasDatabaseName("IX_loyalty_movements_once_per_lot_and_redemption");
    builder.HasIndex(x => x.RedemptionId);
    builder.HasIndex(x => new { x.DebtId, x.Type })
        .IsUnique()
        .HasFilter("\"Type\" IN ('DebtMergeTransferIn', 'DebtMergeTransferOut', 'DebtOpened')")
        .HasDatabaseName("IX_loyalty_movements_once_per_debt");
    builder.HasIndex(x => new { x.DebtId, x.LotId })
        .IsUnique()
        .HasFilter("\"Type\" = 'DebtSettlement'")
        .HasDatabaseName("IX_loyalty_movements_settlement_once");
    builder.HasIndex(x => new { x.CustomerId, x.PerformedAt });
    builder.HasIndex(x => x.LotId);

    builder.HasOne<CustomerProfile>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<LoyaltyLot>().WithMany().HasForeignKey(x => x.LotId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<LoyaltyDebt>().WithMany().HasForeignKey(x => x.DebtId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<LoyaltyAdjustment>().WithMany().HasForeignKey(x => x.AdjustmentId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<User>().WithMany().HasForeignKey(x => x.PerformedByUserId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<LoyaltyRedemption>().WithMany().HasForeignKey(x => x.RedemptionId).OnDelete(DeleteBehavior.Restrict);
  }
}
