using Barbershop.Domain.Customers;
using Barbershop.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

internal sealed class CustomerProfileConfiguration : IEntityTypeConfiguration<CustomerProfile>
{
  public void Configure(EntityTypeBuilder<CustomerProfile> builder)
  {
    builder.ToTable("customers");

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.FullName).HasMaxLength(120).IsRequired();
    builder.Property(x => x.Phone).HasMaxLength(40).IsRequired();
    builder.Property(x => x.NormalizedPhone).HasMaxLength(40).IsRequired();
    builder.Property(x => x.Email).HasMaxLength(256).IsRequired();
    builder.Property(x => x.NormalizedEmail).HasMaxLength(256).IsRequired();
    builder.Property(x => x.LinkedAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.MergedAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.Version).IsRowVersion();
    builder.Ignore(x => x.IsMerged);

    // Not unique: duplicates are allowed (and later merged); the index only speeds up duplicate warnings.
    builder.HasIndex(x => x.NormalizedEmail);
    builder.HasIndex(x => x.NormalizedPhone);

    // An account can back at most one live profile. Merged profiles are excluded so a merge can
    // hand the account over to the surviving profile.
    builder.HasIndex(x => x.UserId)
        .IsUnique()
        .HasFilter("\"UserId\" IS NOT NULL AND \"MergedIntoCustomerId\" IS NULL");

    builder.HasOne<User>()
        .WithMany()
        .HasForeignKey(x => x.UserId)
        .OnDelete(DeleteBehavior.Restrict);

    builder.HasOne<CustomerProfile>()
        .WithMany()
        .HasForeignKey(x => x.MergedIntoCustomerId)
        .OnDelete(DeleteBehavior.Restrict);
  }
}

internal sealed class CustomerEventConfiguration : IEntityTypeConfiguration<CustomerEvent>
{
  public void Configure(EntityTypeBuilder<CustomerEvent> builder)
  {
    builder.ToTable("customer_events");

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(32).IsRequired();
    builder.Property(x => x.Reason).HasMaxLength(500);
    builder.Property(x => x.PerformedAt).HasColumnType("timestamp with time zone").IsRequired();

    builder.HasIndex(x => new { x.CustomerId, x.PerformedAt });

    builder.HasOne<CustomerProfile>()
        .WithMany()
        .HasForeignKey(x => x.CustomerId)
        .OnDelete(DeleteBehavior.Restrict);
  }
}
