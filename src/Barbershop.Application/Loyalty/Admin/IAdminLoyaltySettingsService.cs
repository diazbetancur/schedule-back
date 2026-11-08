using Barbershop.Application.Common.Paging;

namespace Barbershop.Application.Loyalty.Admin;

public interface IAdminLoyaltySettingsService
{
    Task<LoyaltySettingsView> GetAsync(CancellationToken cancellationToken = default);

    Task<LoyaltySettingsView> UpdateAsync(Guid changedByUserId, LoyaltySettingsUpdateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Most recent first, paginated.</summary>
    Task<PagedResult<LoyaltySettingsChangeView>> GetChangesAsync(PageQuery? query = null, CancellationToken cancellationToken = default);
}
