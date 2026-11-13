using Barbershop.Application.Common.Exceptions;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Users;

/// <summary>
/// The platform must always keep an active administrator (BUG-076). Every path that deactivates an account goes
/// through here: an administrator cannot deactivate their own account, and the last active Admin cannot be
/// deactivated. The Admin role itself cannot be removed through the API (system roles are not assignable nor
/// editable), so deactivation is the only way to lose it.
/// </summary>
internal static class AdminAccountGuard
{
  /// <summary>Serialises deactivations, so two administrators deactivating each other cannot both pass the check.</summary>
  private const long DeactivationLockKey = 0x41444D494E53; // "ADMINS"

  public const string SelfDeactivationMessage = "No puedes desactivar tu propia cuenta. Pide a otro administrador que lo haga.";

  public const string LastAdminMessage =
      "No se puede desactivar al último administrador activo: el sistema quedaría sin nadie que pueda administrarlo.";

  /// <summary>
  /// Checks the rules for deactivating <paramref name="targetUserId"/> and runs <paramref name="save"/> (which
  /// persists the deactivation) in the same transaction, under a lock shared by all deactivations.
  /// </summary>
  public static async Task<T> DeactivateAsync<T>(
      AppDbContext dbContext,
      Guid targetUserId,
      Guid? actingUserId,
      Func<Task<T>> save,
      CancellationToken cancellationToken)
  {
    if (actingUserId == targetUserId)
    {
      throw new ConflictException(SelfDeactivationMessage);
    }

    if (!dbContext.Database.IsRelational() || dbContext.Database.CurrentTransaction is not null)
    {
      await CheckAsync(dbContext, targetUserId, cancellationToken);
      return await save();
    }

    await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
    await CheckAsync(dbContext, targetUserId, cancellationToken);
    var result = await save();
    await transaction.CommitAsync(cancellationToken);
    return result;
  }

  private static async Task CheckAsync(AppDbContext dbContext, Guid targetUserId, CancellationToken cancellationToken)
  {
    if (dbContext.Database.IsNpgsql())
    {
      await dbContext.Database.ExecuteSqlRawAsync(
          "SELECT pg_advisory_xact_lock({0})", [DeactivationLockKey], cancellationToken);
    }

    var adminRole = RoleNames.Admin.ToUpperInvariant();
    var activeAdminIds = await dbContext.Users.AsNoTracking()
        .Where(user => user.IsActive && user.UserRoles.Any(userRole => userRole.Role.NormalizedName == adminRole))
        .Select(user => user.Id)
        .Take(2)
        .ToListAsync(cancellationToken);

    if (activeAdminIds is [var onlyAdminId] && onlyAdminId == targetUserId)
    {
      throw new ConflictException(LastAdminMessage);
    }
  }
}
