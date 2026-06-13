using Barbershop.Domain.Appointments;
using Barbershop.Domain.Landing;
using Barbershop.Domain.Media;
using Barbershop.Domain.Scheduling;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<NotificationCampaign> NotificationCampaigns => Set<NotificationCampaign>();
    public DbSet<StaffProfile> StaffProfiles => Set<StaffProfile>();
    public DbSet<StaffService> StaffServices => Set<StaffService>();
    public DbSet<StaffAvailabilityRule> StaffAvailabilityRules => Set<StaffAvailabilityRule>();
    public DbSet<StaffUnavailablePeriod> StaffUnavailablePeriods => Set<StaffUnavailablePeriod>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();
    public DbSet<PendingFileDeletion> PendingFileDeletions => Set<PendingFileDeletion>();
    public DbSet<Banner> Banners => Set<Banner>();
    public DbSet<LandingContent> LandingContents => Set<LandingContent>();
    public DbSet<AppBrandingSettings> AppBrandingSettings => Set<AppBrandingSettings>();
    public DbSet<BusinessScheduleDay> BusinessScheduleDays => Set<BusinessScheduleDay>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("public");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
