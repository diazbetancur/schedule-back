namespace Barbershop.Application.Loyalty.Admin;

public interface IAdminLoyaltySettingsService
{
    Task<LoyaltySettingsView> GetAsync(CancellationToken cancellationToken = default);

    Task<LoyaltySettingsView> UpdateAsync(Guid changedByUserId, LoyaltySettingsUpdateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Most recent first.</summary>
    Task<IReadOnlyList<LoyaltySettingsChangeView>> GetChangesAsync(CancellationToken cancellationToken = default);
}
