using Barbershop.Domain.Landing;
using Barbershop.Domain.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Barbershop.Infrastructure.Persistence.Configurations;

internal sealed class BannerConfiguration : IEntityTypeConfiguration<Banner>
{
  public void Configure(EntityTypeBuilder<Banner> builder)
  {
    builder.ToTable("banners", tableBuilder =>
    {
      tableBuilder.HasCheckConstraint(
              "ck_banners_schedule_window",
              "\"StartsAt\" IS NULL OR \"EndsAt\" IS NULL OR \"StartsAt\" < \"EndsAt\"");
    });

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.Title).HasMaxLength(160).IsRequired();
    builder.Property(x => x.Subtitle).HasMaxLength(300);
    builder.Property(x => x.LinkUrl).HasMaxLength(2048);
    builder.Property(x => x.IsActive).HasDefaultValue(true);
    builder.Property(x => x.StartsAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.EndsAt).HasColumnType("timestamp with time zone");
    builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");

    builder.HasIndex(x => new { x.IsActive, x.SortOrder });

    builder.HasOne<MediaAsset>()
        .WithMany()
        .HasForeignKey(x => x.ImageMediaAssetId)
        .OnDelete(DeleteBehavior.SetNull);
  }
}

internal sealed class LandingContentConfiguration : IEntityTypeConfiguration<LandingContent>
{
  public void Configure(EntityTypeBuilder<LandingContent> builder)
  {
    builder.ToTable("landing_content");

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.HeroTitle).HasMaxLength(200).IsRequired();
    builder.Property(x => x.HeroSubtitle).HasMaxLength(400);
    builder.Property(x => x.AboutTitle).HasMaxLength(200);
    builder.Property(x => x.AboutText).HasMaxLength(4000);
    builder.Property(x => x.ContactPhone).HasMaxLength(40);
    builder.Property(x => x.MapsUrl).HasMaxLength(2000);
    builder.Property(x => x.Address).HasMaxLength(300);
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
  }
}

internal sealed class AppBrandingSettingsConfiguration : IEntityTypeConfiguration<AppBrandingSettings>
{
  public void Configure(EntityTypeBuilder<AppBrandingSettings> builder)
  {
    builder.ToTable("app_branding_settings");

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Id).ValueGeneratedNever();
    builder.Property(x => x.AppName).HasMaxLength(120).IsRequired();
    builder.Property(x => x.PrimaryColor).HasMaxLength(7).IsRequired();
    builder.Property(x => x.SecondaryColor).HasMaxLength(7).IsRequired();
    builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");

    builder.HasOne<MediaAsset>()
        .WithMany()
        .HasForeignKey(x => x.LogoMediaAssetId)
        .OnDelete(DeleteBehavior.SetNull);

    builder.HasOne<MediaAsset>()
        .WithMany()
        .HasForeignKey(x => x.AppIconMediaAssetId)
        .OnDelete(DeleteBehavior.SetNull);
  }
}
