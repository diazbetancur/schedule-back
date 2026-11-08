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

internal sealed class CustomerClaimConfiguration : IEntityTypeConfiguration<CustomerClaim>
{
  public void Configure(EntityTypeBuilder<CustomerClaim> builder)
  {
    // Explicit IS NOT NULL everywhere: a CHECK that evaluates to NULL passes.
    builder.ToTable("customer_claims", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint(
          "ck_customer_claims_status_shape",
          "(\"Status\" = 'Pending' AND \"ApprovedCustomerId\" IS NULL AND \"DecidedAt\" IS NULL AND \"DecidedByUserId\" IS NULL) "
          + "OR (\"Status\" = 'Approved' AND \"ApprovedCustomerId\" IS NOT NULL AND \"DecidedAt\" IS NOT NULL AND \"DecidedByUserId\" IS NOT NULL) "
          + "OR (\"Status\" = 'Rejected' AND \"ApprovedCustomerId\" IS NULL AND \"DecidedAt\" IS NOT NULL AND \"DecidedByUserId\" IS NOT NULL "
          + "AND \"DecisionNotes\" IS NOT NULL)");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.FullName).HasMaxLength(120).IsRequired();
    builder.Property(x => x.Phone).HasMaxLength(40).IsRequired();
    builder.Property(x => x.NormalizedPhone).HasMaxLength(40).IsRequired();
    builder.Property(x => x.Email).HasMaxLength(256).IsRequired();
    builder.Property(x => x.NormalizedEmail).HasMaxLength(256).IsRequired();
    builder.Property(x => x.Notes).HasMaxLength(CustomerClaim.MaxNotesLength);
    builder.Property(x => x.DecisionNotes).HasMaxLength(CustomerClaim.MaxReasonLength);
    builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
    builder.Property(x => x.SubmittedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.DecidedAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.Version).IsRowVersion();

    builder.HasIndex(x => x.IdempotencyKey).IsUnique();

    // One pending claim per account; decided ones stay as history.
    builder.HasIndex(x => x.UserId)
        .IsUnique()
        .HasFilter("\"Status\" = 'Pending'")
        .HasDatabaseName("IX_customer_claims_UserId_pending");
    builder.HasIndex(x => new { x.UserId, x.SubmittedAt });
    builder.HasIndex(x => new { x.Status, x.SubmittedAt });

    builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<User>().WithMany().HasForeignKey(x => x.DecidedByUserId).OnDelete(DeleteBehavior.Restrict);
    builder.HasOne<CustomerProfile>().WithMany().HasForeignKey(x => x.ApprovedCustomerId).OnDelete(DeleteBehavior.Restrict);
  }
}
