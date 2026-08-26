using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Configuration;
using Barbershop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Barbershop.Infrastructure.Identity;

internal sealed class IdentitySeedService : IIdentitySeedService
{
  private static readonly SemaphoreSlim DatabaseEnsureLock = new(1, 1);
  private static volatile bool _databaseEnsured;

  private static readonly IReadOnlyDictionary<string, string> PermissionDescriptions =
      new Dictionary<string, string>(StringComparer.Ordinal)
      {
        [PermissionCodes.SalesRegister] = "Registrar ventas"
      };

  private readonly AppDbContext _dbContext;
  private readonly IPasswordHasher<object> _passwordHasher;
  private readonly IOptions<SeedAdminOptions> _seedAdminOptions;
  private readonly IHostEnvironment _environment;
  private readonly TimeProvider _timeProvider;

  public IdentitySeedService(
      AppDbContext dbContext,
      IPasswordHasher<object> passwordHasher,
      IOptions<SeedAdminOptions> seedAdminOptions,
      IHostEnvironment environment,
      TimeProvider timeProvider)
  {
    _dbContext = dbContext;
    _passwordHasher = passwordHasher;
    _seedAdminOptions = seedAdminOptions;
    _environment = environment;
    _timeProvider = timeProvider;
  }

  public async Task EnsureSeededAsync(CancellationToken cancellationToken = default)
  {
    await EnsureDatabaseAsync(cancellationToken);

    var rolesChanged = await EnsureRolesAsync(cancellationToken);
    if (rolesChanged)
    {
      await _dbContext.SaveChangesAsync(cancellationToken);
    }

    var permissionsChanged = await EnsurePermissionsAsync(cancellationToken);
    if (permissionsChanged)
    {
      await _dbContext.SaveChangesAsync(cancellationToken);
    }

    var adminPermissionsChanged = await EnsureAdminPermissionsAsync(cancellationToken);
    if (adminPermissionsChanged)
    {
      await _dbContext.SaveChangesAsync(cancellationToken);
    }

    var adminChanged = await EnsureAdminAsync(cancellationToken);
    if (adminChanged)
    {
      await _dbContext.SaveChangesAsync(cancellationToken);
    }
  }

  private async Task EnsureDatabaseAsync(CancellationToken cancellationToken)
  {
    if (_databaseEnsured)
    {
      return;
    }

    await DatabaseEnsureLock.WaitAsync(cancellationToken);
    try
    {
      if (_databaseEnsured)
      {
        return;
      }

      if (_dbContext.Database.IsRelational())
      {
        await _dbContext.Database.MigrateAsync(cancellationToken);
      }
      else
      {
        await _dbContext.Database.EnsureCreatedAsync(cancellationToken);
      }

      _databaseEnsured = true;
    }
    finally
    {
      DatabaseEnsureLock.Release();
    }
  }

  private async Task<bool> EnsureRolesAsync(CancellationToken cancellationToken)
  {
    var existingNormalizedNames = await _dbContext.Roles
        .Select(role => role.NormalizedName)
        .ToListAsync(cancellationToken);

    var changed = false;
    foreach (var roleName in RoleNames.All)
    {
      var normalizedRoleName = roleName.ToUpperInvariant();
      if (existingNormalizedNames.Contains(normalizedRoleName, StringComparer.Ordinal))
      {
        continue;
      }

      _dbContext.Roles.Add(new Role(roleName, isSystemRole: true));
      changed = true;
    }

    return changed;
  }

  private async Task<bool> EnsurePermissionsAsync(CancellationToken cancellationToken)
  {
    var existingCodes = await _dbContext.Permissions
        .Select(permission => permission.Code)
        .ToListAsync(cancellationToken);

    var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
    var changed = false;
    foreach (var code in PermissionCodes.All)
    {
      if (existingCodes.Contains(code, StringComparer.Ordinal))
      {
        continue;
      }

      _dbContext.Permissions.Add(new Permission(code, PermissionDescriptions[code], utcNow));
      changed = true;
    }

    return changed;
  }

  private async Task<bool> EnsureAdminPermissionsAsync(CancellationToken cancellationToken)
  {
    var adminRole = await _dbContext.Roles
        .Include(role => role.RolePermissions)
        .SingleAsync(role => role.NormalizedName == RoleNames.Admin.ToUpperInvariant(), cancellationToken);

    var allPermissions = await _dbContext.Permissions.ToListAsync(cancellationToken);
    var grantedPermissionIds = adminRole.RolePermissions.Select(rp => rp.PermissionId).ToHashSet();

    var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
    var changed = false;
    foreach (var permission in allPermissions)
    {
      if (grantedPermissionIds.Contains(permission.Id))
      {
        continue;
      }

      // Bypass Role.AddPermission a propósito: ese método rechaza IsSystemRole por diseño
      // (protege el flujo admin de /admin/roles), pero el seed es quien mantiene a Admin
      // sincronizado con el 100% del catálogo — mismo patrón que EnsureAdminAsync, que ya
      // manipula UserRoles directamente sin pasar por un método de dominio guardado.
      adminRole.RolePermissions.Add(new RolePermission(adminRole.Id, permission.Id, utcNow));
      changed = true;
    }

    return changed;
  }

  private async Task<bool> EnsureAdminAsync(CancellationToken cancellationToken)
  {
    var options = _seedAdminOptions.Value;
    var canSeedInCurrentEnvironment = _environment.IsDevelopment() || _environment.IsEnvironment("Testing") || options.Enabled;

    if (!canSeedInCurrentEnvironment
        || !OptionsValidationHelpers.IsConfigured(options.Email)
        || !OptionsValidationHelpers.IsConfigured(options.Password)
        || !OptionsValidationHelpers.IsConfigured(options.FullName))
    {
      return false;
    }

    var normalizedEmail = options.Email.Trim().ToUpperInvariant();
    var adminRole = await _dbContext.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Admin.ToUpperInvariant(), cancellationToken);

    var existingUser = await _dbContext.Users
        .Include(user => user.UserRoles)
        .ThenInclude(userRole => userRole.Role)
        .SingleOrDefaultAsync(user => user.NormalizedEmail == normalizedEmail, cancellationToken);

    if (existingUser is null)
    {
      var createdAt = _timeProvider.GetUtcNow().UtcDateTime;
      var passwordHash = _passwordHasher.HashPassword(new object(), options.Password);
      var adminUser = new User(options.FullName, options.Email, passwordHash, createdAt);
      adminUser.UserRoles.Add(new UserRole(adminUser.Id, adminRole.Id, createdAt));

      _dbContext.Users.Add(adminUser);
      return true;
    }

    if (existingUser.UserRoles.Any(userRole => userRole.Role.NormalizedName == adminRole.NormalizedName))
    {
      return false;
    }

    existingUser.UserRoles.Add(new UserRole(existingUser.Id, adminRole.Id, _timeProvider.GetUtcNow().UtcDateTime));
    return true;
  }
}
