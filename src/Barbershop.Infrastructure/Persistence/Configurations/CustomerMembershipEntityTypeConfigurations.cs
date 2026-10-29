using Barbershop.Domain.Customers;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Memberships;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

internal sealed class CustomerMembershipConfiguration : IEntityTypeConfiguration<CustomerMembership>
{
  public void Configure(EntityTypeBuilder<CustomerMembership> builder)
  {
    builder.ToTable("customer_memberships", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint("ck_customer_memberships_price_non_negative", "\"Price\" >= 0");
      tableBuilder.HasCheckConstraint("ck_customer_memberships_date_range", "\"EndsOn\" >= \"StartsOn\"");
      // Every compared column is NOT NULL or checked with IS [NOT] NULL, so neither check can evaluate to NULL.
      tableBuilder.HasCheckConstraint(
          "ck_customer_memberships_active_has_activation",
          "\"Status\" <> 'Active' OR \"ActivatedAt\" IS NOT NULL");
      tableBuilder.HasCheckConstraint(
          "ck_customer_memberships_annulled_has_audit",
          "\"Status\" <> 'Annulled' OR (\"AnnulledAt\" IS NOT NULL AND \"AnnulledByUserId\" IS NOT NULL AND \"AnnulmentReason\" IS NOT NULL)");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.PlanName).HasMaxLength(120).IsRequired();
    builder.Property(x => x.PlanDescription).HasMaxLength(1000);
    builder.Property(x => x.Mode).HasConversion<string>().HasMaxLength(16).IsRequired();
    builder.Property(x => x.Terms).HasMaxLength(4000).IsRequired();
    builder.Property(x => x.SpecificConditions).HasMaxLength(2000);
    builder.Property(x => x.SoldOn).HasColumnType("date");
    builder.Property(x => x.StartsOn).HasColumnType("date");
    builder.Property(x => x.EndsOn).HasColumnType("date");
    builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
    builder.Property(x => x.ActivatedAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.AnnulledAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.AnnulmentReason).HasMaxLength(500);
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.Version).IsRowVersion();

    // First-purchase checks look up a customer's memberships of one plan.
    builder.HasIndex(x => new { x.CustomerId, x.MembershipPlanId });
    builder.HasIndex(x => x.IncomeEntryId);

    builder.HasOne<CustomerProfile>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<CustomerProfile>().WithMany().HasForeignKey(x => x.OriginalCustomerId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<MembershipPlan>().WithMany().HasForeignKey(x => x.MembershipPlanId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<IncomeEntry>().WithMany().HasForeignKey(x => x.IncomeEntryId).OnDelete(DeleteBehavior.Restrict);

    builder.HasMany(x => x.Allowances).WithOne().HasForeignKey(x => x.CustomerMembershipId).OnDelete(DeleteBehavior.Restrict);
    builder.HasMany(x => x.Gifts).WithOne().HasForeignKey(x => x.CustomerMembershipId).OnDelete(DeleteBehavior.Restrict);
  }
}

internal sealed class MembershipGiftConfiguration : IEntityTypeConfiguration<MembershipGift>
{
  public void Configure(EntityTypeBuilder<MembershipGift> builder)
  {
    builder.ToTable("membership_gifts", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint("ck_membership_gifts_quantity_range", "\"Quantity\" >= 1 AND \"Quantity\" <= 1000");
      tableBuilder.HasCheckConstraint(
          "ck_membership_gifts_kind_shape",
          "(\"Kind\" = 'Service' AND \"ServiceId\" IS NOT NULL AND \"ServiceNameSnapshot\" IS NOT NULL AND \"DescriptionSnapshot\" IS NULL) "
          + "OR (\"Kind\" = 'Physical' AND \"ServiceId\" IS NULL AND \"ServiceNameSnapshot\" IS NULL AND \"DescriptionSnapshot\" IS NOT NULL)");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
    builder.Property(x => x.Condition).HasConversion<string>().HasMaxLength(32).IsRequired();
    builder.Property(x => x.ServiceNameSnapshot).HasMaxLength(120);
    builder.Property(x => x.DescriptionSnapshot).HasMaxLength(200);
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();

    // One concrete gift per rule and purchase: the same purchase can never be granted a gift twice.
    builder.HasIndex(x => new { x.CustomerMembershipId, x.MembershipPlanGiftRuleId }).IsUnique();

    builder.HasOne<MembershipPlanGiftRule>().WithMany().HasForeignKey(x => x.MembershipPlanGiftRuleId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Restrict);
  }
}

internal sealed class MembershipAllowanceConfiguration : IEntityTypeConfiguration<MembershipAllowance>
{
  public void Configure(EntityTypeBuilder<MembershipAllowance> builder)
  {
    builder.ToTable("membership_allowances", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint(
          "ck_membership_allowances_granted_range", "\"GrantedQuantity\" >= 1 AND \"GrantedQuantity\" <= 1000");
      tableBuilder.HasCheckConstraint(
          "ck_membership_allowances_origin_shape",
          "(\"Origin\" = 'Purchased' AND \"MembershipGiftId\" IS NULL) OR (\"Origin\" = 'Gift' AND \"MembershipGiftId\" IS NOT NULL)");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Origin).HasConversion<string>().HasMaxLength(16).IsRequired();

    // A service gift has exactly one balance line.
    builder.HasIndex(x => x.MembershipGiftId).IsUnique().HasFilter("\"MembershipGiftId\" IS NOT NULL");
    builder.HasIndex(x => x.CustomerMembershipId);

    builder.HasOne<MembershipGift>().WithMany().HasForeignKey(x => x.MembershipGiftId).OnDelete(DeleteBehavior.Restrict);
    builder.HasMany(x => x.Services).WithOne().HasForeignKey(x => x.MembershipAllowanceId).OnDelete(DeleteBehavior.Restrict);
  }
}

internal sealed class MembershipAllowanceServiceConfiguration : IEntityTypeConfiguration<MembershipAllowanceService>
{
  public void Configure(EntityTypeBuilder<MembershipAllowanceService> builder)
  {
    builder.ToTable("membership_allowance_services");

    builder.HasKey(x => new { x.MembershipAllowanceId, x.ServiceId });
    builder.Property(x => x.ServiceNameSnapshot).HasMaxLength(120).IsRequired();

    builder.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Restrict);
  }
}

internal sealed class MembershipConsumptionConfiguration : IEntityTypeConfiguration<MembershipConsumption>
{
  public void Configure(EntityTypeBuilder<MembershipConsumption> builder)
  {
    builder.ToTable("membership_consumptions", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint("ck_membership_consumptions_quantity_range", "\"Quantity\" >= 1 AND \"Quantity\" <= 1000");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.ServiceNameSnapshot).HasMaxLength(120).IsRequired();
    builder.Property(x => x.ServiceDate).HasColumnType("date");
    builder.Property(x => x.RegisteredAt).HasColumnType("timestamp with time zone").IsRequired();

    // A retried request carries the same key, so it can never be stored twice.
    builder.HasIndex(x => x.IdempotencyKey).IsUnique();
    builder.HasIndex(x => new { x.CustomerMembershipId, x.ServiceDate });

    builder.HasOne<CustomerMembership>().WithMany().HasForeignKey(x => x.CustomerMembershipId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<StaffProfile>().WithMany().HasForeignKey(x => x.StaffProfileId).OnDelete(DeleteBehavior.Restrict);
  }
}

internal sealed class MembershipMovementConfiguration : IEntityTypeConfiguration<MembershipMovement>
{
  public void Configure(EntityTypeBuilder<MembershipMovement> builder)
  {
    builder.ToTable("membership_movements", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint("ck_membership_movements_quantity_range", "\"Quantity\" >= 1 AND \"Quantity\" <= 1000");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(16).IsRequired();
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();

    // A consumption takes from each line at most once, and its reversal gives each line back at most once.
    builder.HasIndex(x => new { x.MembershipConsumptionId, x.MembershipAllowanceId, x.Type }).IsUnique();
    builder.HasIndex(x => x.MembershipAllowanceId);
    builder.HasIndex(x => x.CustomerMembershipId);

    builder.HasOne<CustomerMembership>().WithMany().HasForeignKey(x => x.CustomerMembershipId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<MembershipAllowance>().WithMany().HasForeignKey(x => x.MembershipAllowanceId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<MembershipConsumption>().WithMany().HasForeignKey(x => x.MembershipConsumptionId).OnDelete(DeleteBehavior.Restrict);
  }
}

internal sealed class MembershipConsumptionReversalConfiguration : IEntityTypeConfiguration<MembershipConsumptionReversal>
{
  public void Configure(EntityTypeBuilder<MembershipConsumptionReversal> builder)
  {
    builder.ToTable("membership_consumption_reversals");

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Reason).HasMaxLength(500).IsRequired();
    builder.Property(x => x.PerformedAt).HasColumnType("timestamp with time zone").IsRequired();

    // A consumption can be reversed only once.
    builder.HasIndex(x => x.MembershipConsumptionId).IsUnique();

    builder.HasOne<MembershipConsumption>().WithMany().HasForeignKey(x => x.MembershipConsumptionId).OnDelete(DeleteBehavior.Restrict);
  }
}

internal sealed class MembershipEventConfiguration : IEntityTypeConfiguration<MembershipEvent>
{
  public void Configure(EntityTypeBuilder<MembershipEvent> builder)
  {
    builder.ToTable("membership_events");

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(32).IsRequired();
    builder.Property(x => x.Reason).HasMaxLength(500);
    builder.Property(x => x.PerformedAt).HasColumnType("timestamp with time zone").IsRequired();

    builder.HasIndex(x => new { x.CustomerMembershipId, x.PerformedAt });
    builder.HasIndex(x => x.IncomeEntryId);

    builder.HasOne<CustomerMembership>().WithMany().HasForeignKey(x => x.CustomerMembershipId).OnDelete(DeleteBehavior.Restrict);
  }
}
