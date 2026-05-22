using Barbershop.Domain.Appointments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

internal sealed class AppointmentConfiguration : IEntityTypeConfiguration<Appointment>
{
  public void Configure(EntityTypeBuilder<Appointment> builder)
  {
    builder.ToTable("appointments", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint(
              "ck_appointments_time_range",
              "\"StartsAt\" < \"EndsAt\"");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.CustomerName).HasMaxLength(120).IsRequired();
    builder.Property(x => x.CustomerEmail).HasMaxLength(256);
    builder.Property(x => x.CustomerPhone).HasMaxLength(40);
    builder.Property(x => x.Notes).HasMaxLength(2000);
    builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
    builder.Property(x => x.Source).HasConversion<string>().HasMaxLength(24).IsRequired();
    builder.Property(x => x.StartsAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.EndsAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");

    builder.HasOne(x => x.StaffProfile)
        .WithMany(x => x.Appointments)
        .HasForeignKey(x => x.StaffProfileId)
        .OnDelete(DeleteBehavior.Restrict);

    builder.HasIndex(x => new { x.StaffProfileId, x.StartsAt });

    builder.HasOne(x => x.Review)
        .WithOne(x => x.Appointment)
        .HasForeignKey<Review>(x => x.AppointmentId)
        .OnDelete(DeleteBehavior.Cascade);
  }
}

internal sealed class ReviewConfiguration : IEntityTypeConfiguration<Review>
{
  public void Configure(EntityTypeBuilder<Review> builder)
  {
    builder.ToTable("reviews", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint(
              "ck_reviews_stars_range",
              "\"Stars\" BETWEEN 1 AND 5");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Comment).HasMaxLength(2000);
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();

    builder.HasIndex(x => x.AppointmentId).IsUnique();
  }
}