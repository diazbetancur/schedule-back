using Barbershop.Domain.Media;
using Barbershop.Domain.Staff;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

internal sealed class StaffProfileConfiguration : IEntityTypeConfiguration<StaffProfile>
{
  public void Configure(EntityTypeBuilder<StaffProfile> builder)
  {
    builder.ToTable("staff_profiles", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint(
              "ck_staff_profiles_default_duration",
              "\"DefaultAppointmentDurationMinutes\" BETWEEN 10 AND 240");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.DisplayName).HasMaxLength(120).IsRequired();
    builder.Property(x => x.Bio).HasMaxLength(2000);
    builder.Property(x => x.PhoneNumber).HasMaxLength(40);
    builder.Property(x => x.InstagramUrl).HasMaxLength(2048);
    builder.Property(x => x.FacebookUrl).HasMaxLength(2048);
    builder.Property(x => x.TikTokUrl).HasMaxLength(2048);
    builder.Property(x => x.YoutubeUrl).HasMaxLength(2048);
    builder.Property(x => x.XUrl).HasMaxLength(2048);
    builder.Property(x => x.IsActive).HasDefaultValue(true);
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");

    builder.HasIndex(x => x.UserId).IsUnique();

    builder.HasOne<MediaAsset>()
        .WithMany()
        .HasForeignKey(x => x.PhotoMediaAssetId)
        .OnDelete(DeleteBehavior.SetNull);

    builder.HasOne<MediaAsset>()
        .WithMany()
        .HasForeignKey(x => x.TipsQrMediaAssetId)
        .OnDelete(DeleteBehavior.SetNull);

    builder.HasMany(x => x.Services)
        .WithOne(x => x.StaffProfile)
        .HasForeignKey(x => x.StaffProfileId)
        .OnDelete(DeleteBehavior.Cascade);

    builder.HasMany(x => x.AvailabilityRules)
        .WithOne(x => x.StaffProfile)
        .HasForeignKey(x => x.StaffProfileId)
        .OnDelete(DeleteBehavior.Cascade);

    builder.HasMany(x => x.UnavailablePeriods)
        .WithOne(x => x.StaffProfile)
        .HasForeignKey(x => x.StaffProfileId)
        .OnDelete(DeleteBehavior.Cascade);
  }
}

internal sealed class StaffServiceConfiguration : IEntityTypeConfiguration<StaffService>
{
  public void Configure(EntityTypeBuilder<StaffService> builder)
  {
    builder.ToTable("staff_services", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint(
              "ck_staff_services_duration",
              "\"DurationMinutes\" BETWEEN 15 AND 240");
      tableBuilder.HasCheckConstraint(
              "ck_staff_services_price_non_negative",
              "\"Price\" IS NULL OR \"Price\" >= 0");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
    builder.Property(x => x.Description).HasMaxLength(1000);
    builder.Property(x => x.Price).HasPrecision(10, 2);
    builder.Property(x => x.IsActive).HasDefaultValue(true);
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
  }
}
