using Barbershop.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

internal sealed class UserNotificationPreferenceConfiguration : IEntityTypeConfiguration<UserNotificationPreference>
{
  public void Configure(EntityTypeBuilder<UserNotificationPreference> builder)
  {
    builder.ToTable("user_notification_preferences");

    builder.HasKey(x => new { x.UserId, x.Category });

    builder.Property(x => x.Category).HasMaxLength(32).IsRequired();
    builder.Property(x => x.PushEnabled).IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone").IsRequired();

    builder.HasOne<User>()
        .WithMany()
        .HasForeignKey(x => x.UserId)
        .OnDelete(DeleteBehavior.Cascade);
  }
}
