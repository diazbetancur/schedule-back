using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Authorization;

/// <summary>
/// ADR-0018 D1 and D11: nobody approves what benefits themselves. When the actor is the beneficiary, only the Admin role
/// (read from the database, ADR-0005) may go on, and the decision is then marked as self-approved; anyone else gets 403.
/// </summary>
internal static class SelfApprovalGuard
{
    private static readonly string NormalizedAdminRole = RoleNames.Admin.ToUpperInvariant();

    /// <returns>True when the decision is a self-approval by an Admin (mark it); false when the actor is a third party.</returns>
    /// <exception cref="UnauthorizedAccessException">The actor is the beneficiary and is not an Admin.</exception>
    public static async Task<bool> ResolveAsync(
        AppDbContext dbContext, Guid currentUserId, bool isBeneficiary, string forbiddenMessage, CancellationToken cancellationToken)
    {
        if (!isBeneficiary)
        {
            return false;
        }

        var isAdmin = await dbContext.Users.AsNoTracking()
            .AnyAsync(user => user.Id == currentUserId
                && user.UserRoles.Any(userRole => userRole.Role.NormalizedName == NormalizedAdminRole), cancellationToken);

        return isAdmin ? true : throw new UnauthorizedAccessException(forbiddenMessage);
    }

    /// <summary>True when <paramref name="customerId"/> is the customer profile linked to the actor's account.</summary>
    public static Task<bool> IsOwnCustomerProfileAsync(
        AppDbContext dbContext, Guid currentUserId, Guid customerId, CancellationToken cancellationToken)
        => dbContext.Customers.AsNoTracking()
            .AnyAsync(profile => profile.Id == customerId && profile.UserId == currentUserId, cancellationToken);
}
