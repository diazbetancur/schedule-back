using Barbershop.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

internal sealed class UserNotificationConfiguration : IEntityTypeConfiguration<UserNotification>
{
  public void Configure(EntityTypeBuilder<UserNotification> builder)
  {
    builder.ToTable("user_notifications");

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Type).HasMaxLength(64).IsRequired();
    builder.Property(x => x.Title).HasMaxLength(160).IsRequired();
    builder.Property(x => x.Body).HasMaxLength(1000).IsRequired();
    builder.Property(x => x.Url).HasMaxLength(512);
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.ReadAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.DedupKey).HasMaxLength(200);

    // Bell queries: latest N per user, and unread count per user.
    builder.HasIndex(x => new { x.UserId, x.CreatedAt });
    builder.HasIndex(x => new { x.UserId, x.ReadAt });
    builder.HasIndex(x => x.DedupKey).IsUnique();

    builder.HasOne<User>()
        .WithMany()
        .HasForeignKey(x => x.UserId)
        .OnDelete(DeleteBehavior.Cascade);
  }
}
