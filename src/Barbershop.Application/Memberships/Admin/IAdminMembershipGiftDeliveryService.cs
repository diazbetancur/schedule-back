namespace Barbershop.Application.Memberships.Admin;

/// <summary>Registering the delivery of a sold membership's physical gifts and reversing it.</summary>
public interface IAdminMembershipGiftDeliveryService
{
    Task<CustomerMembershipDetailView> DeliverAsync(
        Guid currentUserId,
        Guid membershipId,
        Guid giftId,
        MembershipGiftDeliveryRequest request,
        CancellationToken cancellationToken = default);

    Task<CustomerMembershipDetailView> ReverseAsync(
        Guid currentUserId,
        Guid membershipId,
        Guid giftId,
        Guid deliveryId,
        MembershipGiftDeliveryReverseRequest request,
        CancellationToken cancellationToken = default);
}
