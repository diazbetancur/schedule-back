using Barbershop.Application.Common.Paging;
using Barbershop.Application.Memberships.Admin;

namespace Barbershop.Application.Memberships.Portal;

/// <summary>
/// The signed-in user's own memberships, read-only. The owner is always resolved from the user's currently
/// linked customer profile, never from an identifier sent by the client.
/// </summary>
public interface IMyMembershipsService
{
    /// <summary>Paginated, newest sale first; an empty page when the user has no linked customer profile.</summary>
    Task<PagedResult<MyMembershipSummaryView>> GetMineAsync(
        Guid userId, MembershipListQuery query, CancellationToken cancellationToken = default);

    /// <summary>Not found when the membership does not exist or does not belong to the user's linked profile.</summary>
    Task<MyMembershipView> GetMineByIdAsync(Guid userId, Guid membershipId, CancellationToken cancellationToken = default);
}
