namespace Barbershop.Application.Memberships.Admin;

/// <summary>Registering consumptions of a sold membership's services and reversing them.</summary>
public interface IAdminMembershipLedgerService
{
    Task<CustomerMembershipDetailView> ConsumeAsync(
        Guid currentUserId,
        Guid membershipId,
        MembershipConsumptionRequest request,
        CancellationToken cancellationToken = default);

    Task<CustomerMembershipDetailView> ReverseAsync(
        Guid currentUserId,
        Guid membershipId,
        Guid consumptionId,
        MembershipConsumptionReverseRequest request,
        CancellationToken cancellationToken = default);
}
