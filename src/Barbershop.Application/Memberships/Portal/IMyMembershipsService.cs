namespace Barbershop.Application.Memberships.Portal;

/// <summary>
/// The signed-in user's own memberships, read-only. The owner is always resolved from the user's currently
/// linked customer profile, never from an identifier sent by the client.
/// </summary>
public interface IMyMembershipsService
{
    /// <summary>Empty when the user has no linked customer profile or it has no memberships.</summary>
    Task<IReadOnlyList<MyMembershipSummaryView>> GetMineAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Not found when the membership does not exist or does not belong to the user's linked profile.</summary>
    Task<MyMembershipView> GetMineByIdAsync(Guid userId, Guid membershipId, CancellationToken cancellationToken = default);
}
