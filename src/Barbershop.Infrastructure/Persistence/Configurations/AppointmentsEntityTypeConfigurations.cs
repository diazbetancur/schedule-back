using Barbershop.Domain.Appointments;
using Barbershop.Domain.Services;
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
      // ADR-0016 B3: an appointment booked for a service keeps its service, name and reserved duration together, and
      // ends exactly that duration after it starts. Appointments from before (all three null) are left as they are.
      tableBuilder.HasCheckConstraint(
              "ck_appointments_reserved_service",
              "(\"GeneralServiceId\" IS NULL AND \"GeneralServiceNameSnapshot\" IS NULL AND \"ReservedDurationMinutes\" IS NULL) "
              + "OR (\"GeneralServiceId\" IS NOT NULL AND \"GeneralServiceNameSnapshot\" IS NOT NULL "
              + "AND \"ReservedDurationMinutes\" IS NOT NULL AND \"ReservedDurationMinutes\" >= 10 AND \"ReservedDurationMinutes\" <= 240 "
              + "AND \"EndsAt\" = \"StartsAt\" + make_interval(mins => \"ReservedDurationMinutes\"))");
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
    builder.Property(x => x.GeneralServiceNameSnapshot).HasMaxLength(GeneralService.NameMaxLength);

    builder.HasOne<GeneralService>()
        .WithMany()
        .HasForeignKey(x => x.GeneralServiceId)
        .OnDelete(DeleteBehavior.Restrict);

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