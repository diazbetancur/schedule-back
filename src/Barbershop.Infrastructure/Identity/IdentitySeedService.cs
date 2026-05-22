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

      _dbContext.Roles.Add(new Role(roleName));
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
