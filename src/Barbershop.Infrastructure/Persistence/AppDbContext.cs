using Barbershop.Domain.Appointments;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Landing;
using Barbershop.Domain.Media;
using Barbershop.Domain.Memberships;
using Barbershop.Domain.Scheduling;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<NotificationCampaign> NotificationCampaigns => Set<NotificationCampaign>();
    public DbSet<UserNotification> UserNotifications => Set<UserNotification>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<StaffProfile> StaffProfiles => Set<StaffProfile>();
    public DbSet<StaffService> StaffServices => Set<StaffService>();
    public DbSet<StaffAvailabilityRule> StaffAvailabilityRules => Set<StaffAvailabilityRule>();
    public DbSet<StaffUnavailablePeriod> StaffUnavailablePeriods => Set<StaffUnavailablePeriod>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<IncomeEntry> IncomeEntries => Set<IncomeEntry>();
    public DbSet<FixedExpense> FixedExpenses => Set<FixedExpense>();
    public DbSet<ExpenseEntry> ExpenseEntries => Set<ExpenseEntry>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();
    public DbSet<PendingFileDeletion> PendingFileDeletions => Set<PendingFileDeletion>();
    public DbSet<Banner> Banners => Set<Banner>();
    public DbSet<LandingContent> LandingContents => Set<LandingContent>();
    public DbSet<AppBrandingSettings> AppBrandingSettings => Set<AppBrandingSettings>();
    public DbSet<BusinessScheduleDay> BusinessScheduleDays => Set<BusinessScheduleDay>();
    public DbSet<TickerItem> TickerItems => Set<TickerItem>();
    public DbSet<CustomerProfile> Customers => Set<CustomerProfile>();
    public DbSet<CustomerEvent> CustomerEvents => Set<CustomerEvent>();
    public DbSet<MembershipPlan> MembershipPlans => Set<MembershipPlan>();
    public DbSet<MembershipPlanIncludedService> MembershipPlanServices => Set<MembershipPlanIncludedService>();
    public DbSet<MembershipPlanGiftRule> MembershipPlanGiftRules => Set<MembershipPlanGiftRule>();
    public DbSet<CustomerMembership> CustomerMemberships => Set<CustomerMembership>();
    public DbSet<MembershipGift> MembershipGifts => Set<MembershipGift>();
    public DbSet<MembershipAllowance> MembershipAllowances => Set<MembershipAllowance>();
    public DbSet<MembershipAllowanceService> MembershipAllowanceServices => Set<MembershipAllowanceService>();
    public DbSet<MembershipEvent> MembershipEvents => Set<MembershipEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("public");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
