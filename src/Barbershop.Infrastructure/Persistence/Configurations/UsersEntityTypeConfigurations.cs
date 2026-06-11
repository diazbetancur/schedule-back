using Barbershop.Domain.Appointments;
using Barbershop.Domain.Media;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
  public void Configure(EntityTypeBuilder<User> builder)
  {
    builder.ToTable("users");

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.FullName).HasMaxLength(120).IsRequired();
    builder.Property(x => x.Email).HasMaxLength(256).IsRequired();
    builder.Property(x => x.NormalizedEmail).HasMaxLength(256).IsRequired();
    builder.Property(x => x.PhoneNumber).HasMaxLength(40);
    builder.Property(x => x.PasswordHash).HasMaxLength(1024).IsRequired();
    builder.Property(x => x.DateOfBirth).HasColumnType("date");
    builder.Property(x => x.PasswordResetTokenHash).HasMaxLength(512);
    builder.Property(x => x.PasswordResetTokenExpiresAt).HasColumnType("timestamp with time zone");
    builder.HasIndex(x => x.PasswordResetTokenHash).IsUnique().HasFilter("\"PasswordResetTokenHash\" IS NOT NULL");
    builder.Property(x => x.IsActive).HasDefaultValue(true);
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");

    builder.HasIndex(x => x.NormalizedEmail).IsUnique();

    builder.HasOne<MediaAsset>()
        .WithMany()
        .HasForeignKey(x => x.ProfilePhotoMediaAssetId)
        .OnDelete(DeleteBehavior.SetNull);

    builder.HasOne(x => x.StaffProfile)
        .WithOne(x => x.User)
        .HasForeignKey<StaffProfile>(x => x.UserId)
        .OnDelete(DeleteBehavior.Restrict);

    builder.HasMany(x => x.CustomerAppointments)
        .WithOne(x => x.CustomerUser)
        .HasForeignKey(x => x.CustomerUserId)
        .OnDelete(DeleteBehavior.SetNull);

    builder.HasMany(x => x.Reviews)
        .WithOne(x => x.CustomerUser)
        .HasForeignKey(x => x.CustomerUserId)
        .OnDelete(DeleteBehavior.Restrict);

    builder.HasMany(x => x.RefreshTokens)
        .WithOne(x => x.User)
        .HasForeignKey(x => x.UserId)
        .OnDelete(DeleteBehavior.Cascade);

    builder.HasMany(x => x.PushSubscriptions)
        .WithOne(x => x.User)
        .HasForeignKey(x => x.UserId)
        .OnDelete(DeleteBehavior.Cascade);
  }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
  public void Configure(EntityTypeBuilder<Role> builder)
  {
    builder.ToTable("roles", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint(
              "ck_roles_supported_names",
              "\"NormalizedName\" IN ('ADMIN', 'STAFF', 'CUSTOMER')");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Name).HasMaxLength(50).IsRequired();
    builder.Property(x => x.NormalizedName).HasMaxLength(50).IsRequired();

    builder.HasIndex(x => x.NormalizedName).IsUnique();
  }
}

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
  public void Configure(EntityTypeBuilder<UserRole> builder)
  {
    builder.ToTable("user_roles");

    builder.HasKey(x => new { x.UserId, x.RoleId });

    builder.Property(x => x.AssignedAt).HasColumnType("timestamp with time zone").IsRequired();

    builder.HasOne(x => x.User)
        .WithMany(x => x.UserRoles)
        .HasForeignKey(x => x.UserId)
        .OnDelete(DeleteBehavior.Cascade);

    builder.HasOne(x => x.Role)
        .WithMany(x => x.UserRoles)
        .HasForeignKey(x => x.RoleId)
        .OnDelete(DeleteBehavior.Cascade);
  }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
  public void Configure(EntityTypeBuilder<RefreshToken> builder)
  {
    builder.ToTable("refresh_tokens", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint(
              "ck_refresh_tokens_expiration",
              "\"ExpiresAt\" > \"CreatedAt\"");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.TokenHash).HasMaxLength(512).IsRequired();
    builder.Property(x => x.DeviceLabel).HasMaxLength(120);
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.ExpiresAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.RevokedAt).HasColumnType("timestamp with time zone");

    builder.HasIndex(x => x.TokenHash).IsUnique();
    builder.HasIndex(x => new { x.UserId, x.ExpiresAt });

    builder.HasOne(x => x.User)
        .WithMany(x => x.RefreshTokens)
        .HasForeignKey(x => x.UserId)
        .OnDelete(DeleteBehavior.Cascade);

    builder.HasOne<RefreshToken>()
        .WithMany()
        .HasForeignKey(x => x.ReplacedByRefreshTokenId)
        .OnDelete(DeleteBehavior.Restrict);
  }
}

internal sealed class PushSubscriptionConfiguration : IEntityTypeConfiguration<PushSubscription>
{
  public void Configure(EntityTypeBuilder<PushSubscription> builder)
  {
    builder.ToTable("push_subscriptions");

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Endpoint).HasMaxLength(2048).IsRequired();
    builder.Property(x => x.P256dhKey).HasMaxLength(256).IsRequired();
    builder.Property(x => x.AuthKey).HasMaxLength(256).IsRequired();
    builder.Property(x => x.UserAgent).HasMaxLength(256);
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();

    builder.HasIndex(x => x.Endpoint).IsUnique();
    builder.HasIndex(x => x.UserId);
  }
}

internal sealed class NotificationCampaignConfiguration : IEntityTypeConfiguration<NotificationCampaign>
{
  public void Configure(EntityTypeBuilder<NotificationCampaign> builder)
  {
    builder.ToTable("notification_campaigns");

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Title).HasMaxLength(160).IsRequired();
    builder.Property(x => x.Body).HasMaxLength(1000).IsRequired();
    builder.Property(x => x.TargetSummary).HasMaxLength(280).IsRequired();
    builder.Property(x => x.RecipientCount).IsRequired();
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();

    builder.HasIndex(x => x.CreatedAt);

    builder.HasOne(x => x.SentByUser)
        .WithMany()
        .HasForeignKey(x => x.SentByUserId)
        .OnDelete(DeleteBehavior.Restrict);
  }
}