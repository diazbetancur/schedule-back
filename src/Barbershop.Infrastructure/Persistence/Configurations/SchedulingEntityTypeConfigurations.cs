using Barbershop.Domain.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

internal sealed class StaffAvailabilityRuleConfiguration : IEntityTypeConfiguration<StaffAvailabilityRule>
{
  public void Configure(EntityTypeBuilder<StaffAvailabilityRule> builder)
  {
    builder.ToTable("staff_availability_rules", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint(
              "ck_staff_availability_rules_time_range",
              "\"StartTime\" < \"EndTime\"");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.DayOfWeek).HasConversion<int>().IsRequired();
    builder.Property(x => x.StartTime).HasColumnType("time without time zone").IsRequired();
    builder.Property(x => x.EndTime).HasColumnType("time without time zone").IsRequired();
    builder.Property(x => x.IsActive).HasDefaultValue(true);

    builder.HasIndex(x => new { x.StaffProfileId, x.DayOfWeek });
  }
}

internal sealed class StaffUnavailablePeriodConfiguration : IEntityTypeConfiguration<StaffUnavailablePeriod>
{
  public void Configure(EntityTypeBuilder<StaffUnavailablePeriod> builder)
  {
    builder.ToTable("staff_unavailable_periods", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint(
              "ck_staff_unavailable_periods_time_range",
              "\"StartsAt\" < \"EndsAt\"");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Reason).HasMaxLength(500);
    builder.Property(x => x.StartsAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.EndsAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();

    builder.HasIndex(x => new { x.StaffProfileId, x.StartsAt });
  }
}