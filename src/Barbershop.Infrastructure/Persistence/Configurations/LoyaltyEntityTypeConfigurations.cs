using Barbershop.Domain.Loyalty;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

internal sealed class LoyaltySettingsConfiguration : IEntityTypeConfiguration<LoyaltySettings>
{
  public void Configure(EntityTypeBuilder<LoyaltySettings> builder)
  {
    builder.ToTable("loyalty_settings", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint("ck_loyalty_settings_singleton", $"\"Id\" = {LoyaltySettings.SingletonId}");
      tableBuilder.HasCheckConstraint(
          "ck_loyalty_settings_validity_range",
          $"\"PointsValidityDays\" >= {LoyaltySettings.MinPointsValidityDays} AND \"PointsValidityDays\" <= {LoyaltySettings.MaxPointsValidityDays}");
      tableBuilder.HasCheckConstraint(
          "ck_loyalty_settings_redemption_days_range",
          $"\"RedemptionApprovalDays\" >= {LoyaltySettings.MinRedemptionDays} AND \"RedemptionApprovalDays\" <= {LoyaltySettings.MaxRedemptionDays} "
          + $"AND \"RedemptionDeliveryDays\" >= {LoyaltySettings.MinRedemptionDays} AND \"RedemptionDeliveryDays\" <= {LoyaltySettings.MaxRedemptionDays}");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.IsEnabled).IsRequired();
    builder.Property(x => x.PointsValidityDays).IsRequired();
    // The existing row takes the initial deadlines approved by the user (30 and 30 days, ADR-0015 R7).
    builder.Property(x => x.RedemptionApprovalDays).IsRequired().HasDefaultValue(LoyaltySettings.DefaultRedemptionDays);
    builder.Property(x => x.RedemptionDeliveryDays).IsRequired().HasDefaultValue(LoyaltySettings.DefaultRedemptionDays);
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.Version).IsRowVersion();

    // The single row exists from the migration on: the program starts disabled with the default validity.
    builder.HasData(new
    {
      Id = LoyaltySettings.SingletonId,
      IsEnabled = false,
      PointsValidityDays = LoyaltySettings.DefaultPointsValidityDays,
      RedemptionApprovalDays = LoyaltySettings.DefaultRedemptionDays,
      RedemptionDeliveryDays = LoyaltySettings.DefaultRedemptionDays,
      CreatedAt = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc)
    });
  }
}

internal sealed class LoyaltySettingsChangeConfiguration : IEntityTypeConfiguration<LoyaltySettingsChange>
{
  public void Configure(EntityTypeBuilder<LoyaltySettingsChange> builder)
  {
    builder.ToTable("loyalty_settings_changes");

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.ChangedAt).HasColumnType("timestamp with time zone").IsRequired();

    builder.HasIndex(x => x.ChangedAt);
  }
}

internal sealed class LoyaltyPointsChangeConfiguration : IEntityTypeConfiguration<LoyaltyPointsChange>
{
  public void Configure(EntityTypeBuilder<LoyaltyPointsChange> builder)
  {
    builder.ToTable("loyalty_points_changes", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint(
          "ck_loyalty_points_changes_points_non_negative",
          "\"PreviousPoints\" >= 0 AND \"NewPoints\" >= 0");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.TargetType).HasConversion<string>().HasMaxLength(32).IsRequired();
    builder.Property(x => x.ChangedAt).HasColumnType("timestamp with time zone").IsRequired();

    builder.HasIndex(x => new { x.TargetType, x.TargetId, x.ChangedAt });
  }
}
