namespace Barbershop.Application.Memberships.Admin;

public interface IAdminMembershipPlansService
{
    Task<IReadOnlyList<MembershipPlanView>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<MembershipPlanView> GetByIdAsync(Guid planId, CancellationToken cancellationToken = default);

    Task<MembershipPlanView> CreateAsync(MembershipPlanUpsertRequest request, CancellationToken cancellationToken = default);

    Task<MembershipPlanView> UpdateAsync(Guid planId, MembershipPlanUpsertRequest request, CancellationToken cancellationToken = default);

    Task<MembershipPlanView> UpdateStatusAsync(Guid planId, MembershipPlanStatusUpdateRequest request, CancellationToken cancellationToken = default);

    Task SoftDeleteAsync(Guid planId, CancellationToken cancellationToken = default);
}
