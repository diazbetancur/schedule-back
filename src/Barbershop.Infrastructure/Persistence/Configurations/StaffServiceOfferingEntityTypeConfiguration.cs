using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

/// <summary>The general services each professional offers and their duration (ADR-0016 B2).</summary>
internal sealed class StaffServiceOfferingConfiguration : IEntityTypeConfiguration<StaffServiceOffering>
{
  public void Configure(EntityTypeBuilder<StaffServiceOffering> builder)
  {
    builder.ToTable("staff_service_offerings", table =>
    {
      table.HasCheckConstraint(
          "ck_staff_service_offerings_duration_range",
          $"\"DurationMinutes\" >= {StaffServiceOffering.MinDurationMinutes} AND \"DurationMinutes\" <= {StaffServiceOffering.MaxDurationMinutes}");
    });

    builder.HasKey(x => x.Id);
    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.DurationMinutes).IsRequired();
    builder.Property(x => x.IsActive).IsRequired();
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");

    builder.HasOne<StaffProfile>()
        .WithMany()
        .HasForeignKey(x => x.StaffProfileId)
        .OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<GeneralService>()
        .WithMany()
        .HasForeignKey(x => x.GeneralServiceId)
        .OnDelete(DeleteBehavior.Restrict);

    // A professional offers each general service at most once (the row is reused when it is reactivated).
    builder.HasIndex(x => new { x.StaffProfileId, x.GeneralServiceId }).IsUnique();
    builder.HasIndex(x => x.GeneralServiceId);
  }
}
