using Barbershop.Domain.Memberships;
using Barbershop.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

internal sealed class MembershipPlanConfiguration : IEntityTypeConfiguration<MembershipPlan>
{
  public void Configure(EntityTypeBuilder<MembershipPlan> builder)
  {
    builder.ToTable("membership_plans", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint("ck_membership_plans_price_non_negative", "\"Price\" >= 0");
      tableBuilder.HasCheckConstraint(
          "ck_membership_plans_duration_range",
          "\"DefaultDurationDays\" >= 1 AND \"DefaultDurationDays\" <= 3650");
      // Pool plans carry a shared quantity; per-service plans must not. The explicit IS NOT NULL matters:
      // a CHECK that evaluates to NULL passes, so "PoolQuantity >= 1" alone would accept a missing quantity.
      tableBuilder.HasCheckConstraint(
          "ck_membership_plans_pool_quantity_by_mode",
          "(\"Mode\" = 'Pool' AND \"PoolQuantity\" IS NOT NULL AND \"PoolQuantity\" BETWEEN 1 AND 1000) "
          + "OR (\"Mode\" = 'PerService' AND \"PoolQuantity\" IS NULL)");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
    builder.Property(x => x.Description).HasMaxLength(1000);
    builder.Property(x => x.Mode).HasConversion<string>().HasMaxLength(16).IsRequired();
    builder.Property(x => x.Terms).HasMaxLength(4000).IsRequired();
    builder.Property(x => x.SpecificConditions).HasMaxLength(2000);
    builder.Property(x => x.IsActive).HasDefaultValue(true);
    builder.Property(x => x.IsDeleted).HasDefaultValue(false);
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.Version).IsRowVersion();

    // Backstop: exact-name duplicates among live plans. Case-insensitive uniqueness is enforced in the service layer.
    builder.HasIndex(x => x.Name)
        .IsUnique()
        .HasFilter("\"IsDeleted\" = false");

    builder.HasMany(x => x.Services)
        .WithOne()
        .HasForeignKey(x => x.MembershipPlanId)
        .OnDelete(DeleteBehavior.Cascade);

    builder.HasMany(x => x.GiftRules)
        .WithOne()
        .HasForeignKey(x => x.MembershipPlanId)
        .OnDelete(DeleteBehavior.Restrict);
  }
}

internal sealed class MembershipPlanIncludedServiceConfiguration : IEntityTypeConfiguration<MembershipPlanIncludedService>
{
  public void Configure(EntityTypeBuilder<MembershipPlanIncludedService> builder)
  {
    builder.ToTable("membership_plan_services", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint(
          "ck_membership_plan_services_quantity_range",
          "\"Quantity\" IS NULL OR (\"Quantity\" >= 1 AND \"Quantity\" <= 1000)");
    });

    // One row per service and plan: a service cannot appear twice in the same plan.
    builder.HasKey(x => new { x.MembershipPlanId, x.ServiceId });

    builder.HasOne<Service>()
        .WithMany()
        .HasForeignKey(x => x.ServiceId)
        .OnDelete(DeleteBehavior.Restrict);
  }
}

internal sealed class MembershipPlanGiftRuleConfiguration : IEntityTypeConfiguration<MembershipPlanGiftRule>
{
  public void Configure(EntityTypeBuilder<MembershipPlanGiftRule> builder)
  {
    builder.ToTable("membership_plan_gift_rules", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint(
          "ck_membership_plan_gift_rules_quantity_range",
          "\"Quantity\" >= 1 AND \"Quantity\" <= 1000");
      // A service gift names a service; a physical gift is described in free text.
      tableBuilder.HasCheckConstraint(
          "ck_membership_plan_gift_rules_kind_shape",
          "(\"Kind\" = 'Service' AND \"ServiceId\" IS NOT NULL AND \"Description\" IS NULL) "
          + "OR (\"Kind\" = 'Physical' AND \"ServiceId\" IS NULL AND \"Description\" IS NOT NULL)");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
    builder.Property(x => x.Description).HasMaxLength(200);
    builder.Property(x => x.Condition).HasConversion<string>().HasMaxLength(32).IsRequired();
    builder.Property(x => x.IsDeleted).HasDefaultValue(false);
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");

    builder.HasIndex(x => x.MembershipPlanId);

    builder.HasOne<Service>()
        .WithMany()
        .HasForeignKey(x => x.ServiceId)
        .OnDelete(DeleteBehavior.Restrict);
  }
}
