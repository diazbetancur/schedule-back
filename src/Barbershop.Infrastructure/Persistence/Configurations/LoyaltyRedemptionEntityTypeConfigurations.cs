using Barbershop.Domain.Customers;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.ServiceRecords;
using Barbershop.Domain.Services;
using Barbershop.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

internal sealed class LoyaltyRewardConfiguration : IEntityTypeConfiguration<LoyaltyReward>
{
  public void Configure(EntityTypeBuilder<LoyaltyReward> builder)
  {
    builder.ToTable("loyalty_rewards", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint(
          "ck_loyalty_rewards_cost_range", $"\"PointsCost\" >= 1 AND \"PointsCost\" <= {LoyaltySettings.MaxPointsPerItem}");
      tableBuilder.HasCheckConstraint(
          "ck_loyalty_rewards_kind_shape",
          "(\"Kind\" = 'Service' AND \"ServiceId\" IS NOT NULL) OR (\"Kind\" = 'Other' AND \"ServiceId\" IS NULL)");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Name).HasMaxLength(LoyaltyReward.MaxNameLength).IsRequired();
    builder.Property(x => x.Description).HasMaxLength(LoyaltyReward.MaxDescriptionLength);
    builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.Version).IsRowVersion();

    builder.HasIndex(x => new { x.IsActive, x.Name });

    builder.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<User>().WithMany().HasForeignKey(x => x.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);
  }
}

internal sealed class LoyaltyRewardChangeConfiguration : IEntityTypeConfiguration<LoyaltyRewardChange>
{
  public void Configure(EntityTypeBuilder<LoyaltyRewardChange> builder)
  {
    builder.ToTable("loyalty_reward_changes");

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.ChangeType).HasConversion<string>().HasMaxLength(16).IsRequired();
    builder.Property(x => x.PreviousName).HasMaxLength(LoyaltyReward.MaxNameLength);
    builder.Property(x => x.PreviousDescription).HasMaxLength(LoyaltyReward.MaxDescriptionLength);
    builder.Property(x => x.PreviousKind).HasConversion<string>().HasMaxLength(16);
    builder.Property(x => x.NewName).HasMaxLength(LoyaltyReward.MaxNameLength).IsRequired();
    builder.Property(x => x.NewDescription).HasMaxLength(LoyaltyReward.MaxDescriptionLength);
    builder.Property(x => x.NewKind).HasConversion<string>().HasMaxLength(16).IsRequired();
    builder.Property(x => x.ChangedAt).HasColumnType("timestamp with time zone").IsRequired();

    builder.HasIndex(x => new { x.RewardId, x.ChangedAt });

    builder.HasOne<LoyaltyReward>().WithMany().HasForeignKey(x => x.RewardId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<User>().WithMany().HasForeignKey(x => x.ChangedByUserId).OnDelete(DeleteBehavior.Restrict);
  }
}

internal sealed class LoyaltyRedemptionConfiguration : IEntityTypeConfiguration<LoyaltyRedemption>
{
  public void Configure(EntityTypeBuilder<LoyaltyRedemption> builder)
  {
    // Explicit IS NOT NULL everywhere: a CHECK that evaluates to NULL passes.
    builder.ToTable("loyalty_redemptions", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint(
          "ck_loyalty_redemptions_cost_positive", "\"PointsCost\" > 0");
      tableBuilder.HasCheckConstraint(
          "ck_loyalty_redemptions_days_range",
          $"\"ApprovalDays\" >= {LoyaltySettings.MinRedemptionDays} AND \"ApprovalDays\" <= {LoyaltySettings.MaxRedemptionDays} "
          + $"AND \"DeliveryDays\" >= {LoyaltySettings.MinRedemptionDays} AND \"DeliveryDays\" <= {LoyaltySettings.MaxRedemptionDays}");
      tableBuilder.HasCheckConstraint(
          "ck_loyalty_redemptions_kind_shape",
          "(\"RewardKindSnapshot\" = 'Service' AND \"ServiceIdSnapshot\" IS NOT NULL AND \"ServiceNameSnapshot\" IS NOT NULL) "
          + "OR (\"RewardKindSnapshot\" = 'Other' AND \"ServiceIdSnapshot\" IS NULL AND \"ServiceNameSnapshot\" IS NULL)");
      tableBuilder.HasCheckConstraint(
          "ck_loyalty_redemptions_counter_verified",
          "\"Channel\" <> 'Counter' OR \"InPersonVerified\" = true");
      // Who and when for every state the redemption went through; a service reward is delivered only with its record.
      tableBuilder.HasCheckConstraint(
          "ck_loyalty_redemptions_status_shape",
          "(\"Status\" = 'Requested' AND \"ApprovedAt\" IS NULL AND \"DeliveredAt\" IS NULL AND \"RejectedAt\" IS NULL "
          + "AND \"CancelledAt\" IS NULL AND \"ExpiredAt\" IS NULL) "
          + "OR (\"Status\" = 'Approved' AND \"ApprovedAt\" IS NOT NULL AND \"ApprovedByUserId\" IS NOT NULL "
          + "AND \"ApprovedOn\" IS NOT NULL AND \"DeliverBy\" IS NOT NULL AND \"DeliveredAt\" IS NULL AND \"RejectedAt\" IS NULL "
          + "AND \"CancelledAt\" IS NULL AND \"ExpiredAt\" IS NULL) "
          + "OR (\"Status\" = 'Delivered' AND \"ApprovedAt\" IS NOT NULL AND \"DeliveredAt\" IS NOT NULL "
          + "AND \"DeliveredByUserId\" IS NOT NULL AND \"RejectedAt\" IS NULL AND \"CancelledAt\" IS NULL AND \"ExpiredAt\" IS NULL "
          + "AND ((\"RewardKindSnapshot\" = 'Service' AND \"ServiceRecordId\" IS NOT NULL) "
          + "OR (\"RewardKindSnapshot\" = 'Other' AND \"ServiceRecordId\" IS NULL))) "
          + "OR (\"Status\" = 'Rejected' AND \"RejectedAt\" IS NOT NULL AND \"RejectedByUserId\" IS NOT NULL "
          + "AND \"RejectionReason\" IS NOT NULL AND \"ApprovedAt\" IS NULL AND \"DeliveredAt\" IS NULL "
          + "AND \"CancelledAt\" IS NULL AND \"ExpiredAt\" IS NULL) "
          + "OR (\"Status\" = 'Cancelled' AND \"CancelledAt\" IS NOT NULL AND \"CancelledByUserId\" IS NOT NULL "
          + "AND \"DeliveredAt\" IS NULL AND \"RejectedAt\" IS NULL AND \"ExpiredAt\" IS NULL) "
          + "OR (\"Status\" = 'Expired' AND \"ExpiredAt\" IS NOT NULL AND \"DeliveredAt\" IS NULL "
          + "AND \"RejectedAt\" IS NULL AND \"CancelledAt\" IS NULL)");
      tableBuilder.HasCheckConstraint(
          "ck_loyalty_redemptions_service_record_shape",
          "\"ServiceRecordId\" IS NULL OR (\"Status\" = 'Delivered' AND \"RewardKindSnapshot\" = 'Service')");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.RewardNameSnapshot).HasMaxLength(LoyaltyReward.MaxNameLength).IsRequired();
    builder.Property(x => x.RewardKindSnapshot).HasConversion<string>().HasMaxLength(16).IsRequired();
    builder.Property(x => x.ServiceNameSnapshot).HasMaxLength(120);
    builder.Property(x => x.Channel).HasConversion<string>().HasMaxLength(16).IsRequired();
    builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
    builder.Property(x => x.RequestedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.RequestedOn).HasColumnType("date");
    builder.Property(x => x.ApproveBy).HasColumnType("date");
    builder.Property(x => x.ApprovedAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.ApprovedOn).HasColumnType("date");
    builder.Property(x => x.DeliverBy).HasColumnType("date");
    builder.Property(x => x.DeliveredAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.RejectedAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.RejectionReason).HasMaxLength(LoyaltyRedemption.MaxReasonLength);
    builder.Property(x => x.CancelledAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.CancellationReason).HasMaxLength(LoyaltyRedemption.MaxReasonLength);
    builder.Property(x => x.ExpiredAt).HasColumnType("timestamp with time zone");
    builder.Ignore(x => x.IsPending);
    builder.Ignore(x => x.CurrentDeadline);
    builder.Property(x => x.Version).IsRowVersion();

    // A retried request of the same person returns the stored one.
    builder.HasIndex(x => new { x.RequestedByUserId, x.IdempotencyKey }).IsUnique();
    // A service record delivers at most one redemption.
    builder.HasIndex(x => x.ServiceRecordId).IsUnique().HasFilter("\"ServiceRecordId\" IS NOT NULL");
    builder.HasIndex(x => new { x.CustomerId, x.Status });
    builder.HasIndex(x => new { x.Status, x.RequestedAt });

    builder.HasOne<CustomerProfile>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<LoyaltyReward>().WithMany().HasForeignKey(x => x.RewardId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<User>().WithMany().HasForeignKey(x => x.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<User>().WithMany().HasForeignKey(x => x.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<User>().WithMany().HasForeignKey(x => x.DeliveredByUserId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<User>().WithMany().HasForeignKey(x => x.RejectedByUserId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<User>().WithMany().HasForeignKey(x => x.CancelledByUserId).OnDelete(DeleteBehavior.Restrict);
  }
}

internal sealed class LoyaltyRedemptionAllocationConfiguration : IEntityTypeConfiguration<LoyaltyRedemptionAllocation>
{
  public void Configure(EntityTypeBuilder<LoyaltyRedemptionAllocation> builder)
  {
    builder.ToTable("loyalty_redemption_allocations", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint("ck_loyalty_redemption_allocations_points_positive", "\"Points\" > 0");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();

    // One row per lot of a redemption: what it reserved there.
    builder.HasIndex(x => new { x.RedemptionId, x.LotId }).IsUnique();
    builder.HasIndex(x => x.LotId);

    builder.HasOne<LoyaltyRedemption>().WithMany().HasForeignKey(x => x.RedemptionId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<LoyaltyLot>().WithMany().HasForeignKey(x => x.LotId).OnDelete(DeleteBehavior.Restrict);
  }
}
