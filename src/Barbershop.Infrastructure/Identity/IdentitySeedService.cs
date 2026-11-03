using System.Collections.Concurrent;
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

  // Keyed by connection string: one process can talk to more than one database (integration tests
  // run against a fresh Testcontainers database), so "already migrated" must be tracked per database.
  private static readonly ConcurrentDictionary<string, bool> MigratedDatabases = new(StringComparer.Ordinal);

  private static readonly IReadOnlyDictionary<string, string> PermissionDescriptions =
      new Dictionary<string, string>(StringComparer.Ordinal)
      {
        [PermissionCodes.SalesRegister] = "Registrar ventas",
        [PermissionCodes.ServiceRecordsRegister] = "Registrar servicios realizados",
        [PermissionCodes.ServiceRecordsApprove] = "Aprobar servicios realizados",
        [PermissionCodes.LoyaltySettingsManage] = "Configurar el programa de fidelización",
        [PermissionCodes.LoyaltyRewardsManage] = "Gestionar el catálogo de recompensas",
        [PermissionCodes.LoyaltyRedemptionsApprove] = "Aprobar o rechazar redenciones de puntos",
        [PermissionCodes.LoyaltyRedemptionsDeliver] = "Confirmar la entrega de redenciones de puntos",
        [PermissionCodes.LoyaltyPointsAdjust] = "Ajustar puntos de clientes",
        [PermissionCodes.MembershipPaymentsConfirm] = "Confirmar el pago de membresías",
        [PermissionCodes.CustomerClaimsApprove] = "Aprobar reclamaciones de perfiles de cliente"
      };

  // Professionals register the services they performed; every administrative permission stays out of Staff.
  private static readonly IReadOnlyCollection<string> StaffPermissionCodes = [PermissionCodes.ServiceRecordsRegister];

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

  /// <summary>
  /// Idempotent bootstrap, run at startup: migrations, system roles, the permission catalog with its default grants,
  /// and the configured seed admin. Default grants are given only when the permission or the role is created in this
  /// same run, so a grant an administrator removed later is never restored.
  /// </summary>
  public async Task EnsureSeededAsync(CancellationToken cancellationToken = default)
  {
    await EnsureDatabaseAsync(cancellationToken);

    if (await EnsureRolesAndPermissionsAsync(cancellationToken))
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
    if (!_dbContext.Database.IsRelational())
    {
      // EF InMemory (unit tests): cheap and per-context, so never cached. Caching it used to flip a
      // process-wide flag that made the next real PostgreSQL database skip its migrations.
      await _dbContext.Database.EnsureCreatedAsync(cancellationToken);
      return;
    }

    var databaseKey = _dbContext.Database.GetConnectionString() ?? string.Empty;
    if (MigratedDatabases.ContainsKey(databaseKey))
    {
      return;
    }

    await DatabaseEnsureLock.WaitAsync(cancellationToken);
    try
    {
      if (MigratedDatabases.ContainsKey(databaseKey))
      {
        return;
      }

      await _dbContext.Database.MigrateAsync(cancellationToken);
      MigratedDatabases[databaseKey] = true;
    }
    finally
    {
      DatabaseEnsureLock.Release();
    }
  }

  /// <summary>
  /// Adds the missing system roles and catalog permissions, and the default grants that go with them: Admin gets every
  /// permission, Staff gets <see cref="StaffPermissionCodes"/>. A grant is added only for a permission created now or
  /// to a role created now. An existing permission that an existing role lacks was revoked on purpose (since the catalog
  /// exists, every permission has been granted to Admin when it was created), so it stays revoked. Everything is saved
  /// together by the caller, so a failure cannot leave a new permission without its default grants.
  /// </summary>
  private async Task<bool> EnsureRolesAndPermissionsAsync(CancellationToken cancellationToken)
  {
    var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
    var changed = false;

    var roles = await _dbContext.Roles
        .Include(role => role.RolePermissions)
        .ToListAsync(cancellationToken);
    var createdRoles = new HashSet<Role>();
    foreach (var roleName in RoleNames.All)
    {
      var normalizedRoleName = roleName.ToUpperInvariant();
      if (roles.Any(role => role.NormalizedName == normalizedRoleName))
      {
        continue;
      }

      var role = new Role(roleName, isSystemRole: true);
      _dbContext.Roles.Add(role);
      roles.Add(role);
      createdRoles.Add(role);
      changed = true;
    }

    var permissions = await _dbContext.Permissions.ToListAsync(cancellationToken);
    var createdPermissions = new HashSet<Permission>();
    foreach (var code in PermissionCodes.All)
    {
      if (permissions.Any(permission => permission.Code == code))
      {
        continue;
      }

      var permission = new Permission(code, PermissionDescriptions[code], utcNow);
      _dbContext.Permissions.Add(permission);
      permissions.Add(permission);
      createdPermissions.Add(permission);
      changed = true;
    }

    changed |= GrantDefaults(RoleNames.Admin, PermissionCodes.All);
    changed |= GrantDefaults(RoleNames.Staff, StaffPermissionCodes);
    return changed;

    bool GrantDefaults(string roleName, IReadOnlyCollection<string> defaultCodes)
    {
      var role = roles.Single(candidate => candidate.NormalizedName == roleName.ToUpperInvariant());
      var granted = role.RolePermissions.Select(rolePermission => rolePermission.PermissionId).ToHashSet();
      var added = false;
      foreach (var permission in permissions.Where(permission => defaultCodes.Contains(permission.Code)))
      {
        if (granted.Contains(permission.Id) || (!createdRoles.Contains(role) && !createdPermissions.Contains(permission)))
        {
          continue;
        }

        // Bypass Role.AddPermission a propósito: ese método rechaza IsSystemRole por diseño (protege el flujo
        // admin de /admin/roles), pero el seed es quien da a Admin y a Staff sus permisos iniciales.
        role.RolePermissions.Add(new RolePermission(role.Id, permission.Id, utcNow));
        added = true;
      }

      return added;
    }
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
