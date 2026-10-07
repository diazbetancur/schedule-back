using Barbershop.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

internal sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
  public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
  {
    builder.ToTable("notification_deliveries");

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Channel).HasMaxLength(16).IsRequired();
    builder.Property(x => x.Title).HasMaxLength(160).IsRequired();
    builder.Property(x => x.Body).HasMaxLength(1000).IsRequired();
    builder.Property(x => x.Url).HasMaxLength(512);
    builder.Property(x => x.Tag).HasMaxLength(120);
    builder.Property(x => x.Status).HasMaxLength(16).IsRequired();
    builder.Property(x => x.LastError).HasMaxLength(500);
    builder.Property(x => x.DeliveredTargets).HasMaxLength(2000);
    builder.Property(x => x.NextAttemptAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.ExpiresAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.CompletedAt).HasColumnType("timestamp with time zone");

    // Worker query: pending deliveries that are due.
    builder.HasIndex(x => new { x.Status, x.NextAttemptAt });
    builder.HasIndex(x => x.UserId);

    builder.HasOne<User>()
        .WithMany()
        .HasForeignKey(x => x.UserId)
        .OnDelete(DeleteBehavior.Cascade);

    builder.HasOne<UserNotification>()
        .WithMany()
        .HasForeignKey(x => x.UserNotificationId)
        .OnDelete(DeleteBehavior.SetNull);
  }
}
