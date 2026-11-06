using Barbershop.Domain.Appointments;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.Memberships;
using Barbershop.Domain.ServiceRecords;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

internal sealed class ServiceRecordConfiguration : IEntityTypeConfiguration<ServiceRecord>
{
  public void Configure(EntityTypeBuilder<ServiceRecord> builder)
  {
    // Explicit IS NOT NULL everywhere: a CHECK that evaluates to NULL passes.
    builder.ToTable("service_records", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint(
          "ck_service_records_status_shape",
          "(\"Status\" = 'Pending' AND \"OperationType\" IS NULL AND \"ApprovedAt\" IS NULL AND \"RejectedAt\" IS NULL) "
          + "OR (\"Status\" = 'Approved' AND \"OperationType\" IS NOT NULL AND \"ApprovedAt\" IS NOT NULL "
          + "AND \"ApprovedByUserId\" IS NOT NULL AND \"ApprovedServiceId\" IS NOT NULL AND \"ApprovedStaffProfileId\" IS NOT NULL "
          + "AND \"ApprovedPerformedOn\" IS NOT NULL AND \"RejectedAt\" IS NULL) "
          + "OR (\"Status\" = 'Rejected' AND \"OperationType\" IS NULL AND \"RejectionReason\" IS NOT NULL "
          + "AND \"RejectedAt\" IS NOT NULL AND \"RejectedByUserId\" IS NOT NULL AND \"ApprovedAt\" IS NULL)");
      tableBuilder.HasCheckConstraint(
          "ck_service_records_operation_shape",
          "\"OperationType\" IS NULL "
          + "OR (\"OperationType\" = 'Paid' AND \"ChargedAmount\" IS NOT NULL AND \"ChargedAmount\" > 0 "
          + "AND \"IncomeEntryId\" IS NOT NULL AND \"CustomerMembershipId\" IS NULL AND \"MembershipConsumptionId\" IS NULL) "
          + "OR (\"OperationType\" = 'MembershipConsumption' AND \"ChargedAmount\" IS NULL AND \"IncomeEntryId\" IS NULL "
          + "AND \"ApprovedCustomerId\" IS NOT NULL AND \"CustomerMembershipId\" IS NOT NULL AND \"MembershipConsumptionId\" IS NOT NULL) "
          + "OR (\"OperationType\" = 'Courtesy' AND \"ChargedAmount\" IS NULL AND \"IncomeEntryId\" IS NULL "
          + "AND \"CustomerMembershipId\" IS NULL AND \"MembershipConsumptionId\" IS NULL) "
          + "OR (\"OperationType\" = 'LoyaltyReward' AND \"ChargedAmount\" IS NULL AND \"IncomeEntryId\" IS NULL "
          + "AND \"ApprovedCustomerId\" IS NOT NULL AND \"CustomerMembershipId\" IS NULL AND \"MembershipConsumptionId\" IS NULL)");
      // Only a loyalty reward names a redemption and keeps the commission the business covers (ADR-0015 R13, R14).
      tableBuilder.HasCheckConstraint(
          "ck_service_records_reward_shape",
          "(\"OperationType\" IS NOT NULL AND \"OperationType\" = 'LoyaltyReward' AND \"LoyaltyRedemptionId\" IS NOT NULL "
          + "AND \"RewardBasePriceSnapshot\" IS NOT NULL AND \"RewardBasePriceSnapshot\" >= 0 "
          + "AND \"RewardBusinessPercentageSnapshot\" IS NOT NULL AND \"RewardBusinessPercentageSnapshot\" >= 0 "
          + "AND \"RewardBusinessPercentageSnapshot\" <= 100 "
          + "AND \"RewardProfessionalCommission\" IS NOT NULL AND \"RewardProfessionalCommission\" >= 0) "
          + "OR ((\"OperationType\" IS NULL OR \"OperationType\" <> 'LoyaltyReward') AND \"LoyaltyRedemptionId\" IS NULL "
          + "AND \"RewardBasePriceSnapshot\" IS NULL AND \"RewardBusinessPercentageSnapshot\" IS NULL "
          + "AND \"RewardProfessionalCommission\" IS NULL)");
      tableBuilder.HasCheckConstraint(
          "ck_service_records_loyalty_points",
          "\"AccruesLoyaltyPoints\" = false "
          + "OR (\"OperationType\" = 'Paid' AND \"ApprovedCustomerId\" IS NOT NULL "
          + "AND \"LoyaltyPointsSnapshot\" IS NOT NULL AND \"LoyaltyPointsSnapshot\" > 0)");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.ServiceNameSnapshot).HasMaxLength(120).IsRequired();
    builder.Property(x => x.ApprovedServiceNameSnapshot).HasMaxLength(120);
    builder.Property(x => x.CustomerNameSnapshot).HasMaxLength(120);
    builder.Property(x => x.Notes).HasMaxLength(ServiceRecord.MaxNotesLength);
    builder.Property(x => x.ReviewNotes).HasMaxLength(ServiceRecord.MaxNotesLength);
    builder.Property(x => x.RejectionReason).HasMaxLength(ServiceRecord.MaxReasonLength);
    builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
    builder.Property(x => x.OperationType).HasConversion<string>().HasMaxLength(32);
    builder.Property(x => x.AccruesLoyaltyPoints).HasDefaultValue(false);
    builder.Property(x => x.RegisteredAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.ApprovedAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.RejectedAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.Version).IsRowVersion();

    // A retried registration is recognized by its key; each service of it has its own position.
    builder.HasIndex(x => new { x.RegistrationKey, x.Position }).IsUnique();

    // Each confirmed record owns its income or consumption, and nothing else may share it.
    builder.HasIndex(x => x.IncomeEntryId).IsUnique().HasFilter("\"IncomeEntryId\" IS NOT NULL");
    builder.HasIndex(x => x.MembershipConsumptionId).IsUnique().HasFilter("\"MembershipConsumptionId\" IS NOT NULL");
    builder.HasIndex(x => x.LoyaltyRedemptionId).IsUnique().HasFilter("\"LoyaltyRedemptionId\" IS NOT NULL");

    builder.HasIndex(x => new { x.Status, x.RegisteredAt });
    builder.HasIndex(x => new { x.StaffProfileId, x.RegisteredAt });
    builder.HasIndex(x => x.AppointmentId);

    builder.HasOne<StaffProfile>().WithMany().HasForeignKey(x => x.StaffProfileId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<Appointment>().WithMany().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<CustomerProfile>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<StaffProfile>().WithMany().HasForeignKey(x => x.ApprovedStaffProfileId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<Service>().WithMany().HasForeignKey(x => x.ApprovedServiceId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<CustomerProfile>().WithMany().HasForeignKey(x => x.ApprovedCustomerId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<IncomeEntry>().WithMany().HasForeignKey(x => x.IncomeEntryId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<CustomerMembership>().WithMany().HasForeignKey(x => x.CustomerMembershipId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<MembershipConsumption>().WithMany().HasForeignKey(x => x.MembershipConsumptionId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<LoyaltyRedemption>().WithMany().HasForeignKey(x => x.LoyaltyRedemptionId).OnDelete(DeleteBehavior.Restrict);
  }
}
