using Barbershop.Domain.Media;
using Barbershop.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

internal sealed class MediaAssetConfiguration : IEntityTypeConfiguration<MediaAsset>
{
  public void Configure(EntityTypeBuilder<MediaAsset> builder)
  {
    builder.ToTable("media_assets");

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.FileName).HasMaxLength(260).IsRequired();
    builder.Property(x => x.ContentType).HasMaxLength(150).IsRequired();
    builder.Property(x => x.StorageKey).HasMaxLength(512).IsRequired();
    builder.Property(x => x.PublicUrl).HasMaxLength(2048);
    builder.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(32).IsRequired();
    builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
    builder.Property(x => x.UploadedByUserId).IsRequired();
    builder.Property(x => x.FailureReason).HasMaxLength(500);
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");

    builder.HasOne<User>()
        .WithMany()
        .HasForeignKey(x => x.UploadedByUserId)
        .OnDelete(DeleteBehavior.Restrict);

    builder.HasIndex(x => x.Purpose);
    builder.HasIndex(x => x.Status);
    builder.HasIndex(x => x.UploadedByUserId);
  }
}

internal sealed class PendingFileDeletionConfiguration : IEntityTypeConfiguration<PendingFileDeletion>
{
  public void Configure(EntityTypeBuilder<PendingFileDeletion> builder)
  {
    builder.ToTable("pending_file_deletions");

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.StorageKey).HasMaxLength(512).IsRequired();
    builder.Property(x => x.Reason).HasMaxLength(1000);
    builder.Property(x => x.Attempts).HasDefaultValue(0).IsRequired();
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
  }
}
