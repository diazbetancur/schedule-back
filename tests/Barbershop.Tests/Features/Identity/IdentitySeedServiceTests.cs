using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Configuration;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Barbershop.Tests.Features.Identity;

public sealed class IdentitySeedServiceTests : IDisposable
{
  private readonly AppDbContext _dbContext;
  private readonly IdentitySeedService _seedService;

  public IdentitySeedServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    _seedService = new IdentitySeedService(
        _dbContext,
        new PasswordHasher<object>(),
        Options.Create(new SeedAdminOptions()),
        new TestHostEnvironment(),
        TimeProvider.System);
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task EnsureSeededAsync_SeedsFullPermissionCatalog()
  {
    await _seedService.EnsureSeededAsync();

    var codes = await _dbContext.Permissions.Select(p => p.Code).ToListAsync();
    Assert.Equal(PermissionCodes.All.OrderBy(c => c), codes.OrderBy(c => c));
  }

  [Fact]
  public async Task EnsureSeededAsync_GrantsAdminRole_AllPermissions()
  {
    await _seedService.EnsureSeededAsync();

    var adminRole = await _dbContext.Roles
        .Include(r => r.RolePermissions)
        .SingleAsync(r => r.NormalizedName == RoleNames.Admin.ToUpperInvariant());

    Assert.Equal(PermissionCodes.All.Count, adminRole.RolePermissions.Count);
  }

  [Fact]
  public async Task EnsureSeededAsync_CalledTwice_DoesNotDuplicatePermissionsOrGrants()
  {
    await _seedService.EnsureSeededAsync();
    await _seedService.EnsureSeededAsync();

    Assert.Equal(PermissionCodes.All.Count, await _dbContext.Permissions.CountAsync());

    var adminRole = await _dbContext.Roles
        .Include(r => r.RolePermissions)
        .SingleAsync(r => r.NormalizedName == RoleNames.Admin.ToUpperInvariant());
    Assert.Equal(PermissionCodes.All.Count, adminRole.RolePermissions.Count);
  }

  [Fact]
  public async Task EnsureSeededAsync_GrantsStaffRole_OnlyServiceRecordRegistration()
  {
    await _seedService.EnsureSeededAsync();

    Assert.Equal([PermissionCodes.ServiceRecordsRegister], await GrantedCodesAsync(RoleNames.Staff));
  }

  [Fact]
  public async Task EnsureSeededAsync_GrantsStaffAndCustomer_NoAdministrativePermission()
  {
    await _seedService.EnsureSeededAsync();

    string[] administrative =
    [
      PermissionCodes.ServiceRecordsApprove,
      PermissionCodes.LoyaltySettingsManage,
      PermissionCodes.LoyaltyRewardsManage,
      PermissionCodes.LoyaltyRedemptionsApprove,
      PermissionCodes.LoyaltyRedemptionsDeliver,
      PermissionCodes.LoyaltyPointsAdjust,
      PermissionCodes.MembershipPaymentsConfirm,
      PermissionCodes.CustomerClaimsApprove,
      PermissionCodes.SalesRegister,
    ];

    Assert.Empty((await GrantedCodesAsync(RoleNames.Staff)).Intersect(administrative));
    Assert.Empty(await GrantedCodesAsync(RoleNames.Customer));
  }

  [Fact]
  public async Task EnsureSeededAsync_CalledTwice_DoesNotDuplicateStaffGrant()
  {
    await _seedService.EnsureSeededAsync();
    await _seedService.EnsureSeededAsync();

    Assert.Equal([PermissionCodes.ServiceRecordsRegister], await GrantedCodesAsync(RoleNames.Staff));
    Assert.Equal(PermissionCodes.All.Count, (await GrantedCodesAsync(RoleNames.Admin)).Length);
  }

  [Fact]
  public async Task EnsureSeededAsync_RestoresAStaffGrantRemovedFromTheDatabase()
  {
    await _seedService.EnsureSeededAsync();
    var staffRole = await _dbContext.Roles.Include(r => r.RolePermissions)
        .SingleAsync(r => r.NormalizedName == RoleNames.Staff.ToUpperInvariant());
    _dbContext.RolePermissions.RemoveRange(staffRole.RolePermissions);
    await _dbContext.SaveChangesAsync();

    await _seedService.EnsureSeededAsync();

    Assert.Equal([PermissionCodes.ServiceRecordsRegister], await GrantedCodesAsync(RoleNames.Staff));
  }

  private async Task<string[]> GrantedCodesAsync(string roleName)
  {
    var normalizedName = roleName.ToUpperInvariant();
    return await _dbContext.RolePermissions
        .Where(rp => rp.Role.NormalizedName == normalizedName)
        .Select(rp => rp.Permission.Code)
        .OrderBy(code => code)
        .ToArrayAsync();
  }

  [Fact]
  public async Task EnsureSeededAsync_MarksTheThreeSystemRoles_AsIsSystemRole()
  {
    await _seedService.EnsureSeededAsync();

    var systemRoleCount = await _dbContext.Roles.CountAsync(r => r.IsSystemRole);
    Assert.Equal(3, systemRoleCount);
  }

  private sealed class TestHostEnvironment : IHostEnvironment
  {
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "Barbershop.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(AppContext.BaseDirectory);
  }
}
