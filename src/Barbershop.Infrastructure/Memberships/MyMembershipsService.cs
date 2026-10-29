using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Application.Memberships.Portal;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Memberships;

/// <summary>
/// Customer portal queries. Balances, statuses and gift states come from the same admin computation
/// (<see cref="IAdminMembershipsService.GetByIdAsync"/>), so both sides always agree; this class only decides
/// ownership and leaves the admin-only data out.
/// </summary>
internal sealed class MyMembershipsService : IMyMembershipsService
{
    private const string NotFoundMessage = "La membresía no existe.";

    private readonly AppDbContext _dbContext;
    private readonly IAdminMembershipsService _memberships;

    public MyMembershipsService(AppDbContext dbContext, IAdminMembershipsService memberships)
    {
        _dbContext = dbContext;
        _memberships = memberships;
    }

    public async Task<IReadOnlyList<MyMembershipSummaryView>> GetMineAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var customerId = await FindLinkedCustomerIdAsync(userId, cancellationToken);
        if (customerId is null)
        {
            return [];
        }

        var summaries = await _memberships.GetCustomerMembershipsAsync(customerId.Value, cancellationToken);
        var result = new List<MyMembershipSummaryView>(summaries.Count);
        foreach (var summary in summaries)
        {
            var detail = await _memberships.GetByIdAsync(summary.Id, cancellationToken);
            int Sum(string origin, Func<MembershipAllowanceView, int> pick)
                => detail.Allowances.Where(line => line.Origin == origin).Sum(pick);

            result.Add(new MyMembershipSummaryView(
                detail.Id,
                detail.PlanName,
                detail.DisplayStatus,
                detail.StartsOn,
                detail.EndsOn,
                Sum("Purchased", line => line.GrantedQuantity),
                Sum("Purchased", line => line.AvailableQuantity),
                Sum("Gift", line => line.GrantedQuantity),
                Sum("Gift", line => line.AvailableQuantity),
                detail.Gifts.Count(gift => gift.Status == "PendingDelivery")));
        }

        return result;
    }

    public async Task<MyMembershipView> GetMineByIdAsync(Guid userId, Guid membershipId, CancellationToken cancellationToken = default)
    {
        var customerId = await FindLinkedCustomerIdAsync(userId, cancellationToken) ?? throw new NotFoundException(NotFoundMessage);

        // Someone else's membership answers exactly like a missing one, so its existence is not revealed.
        var owned = await _dbContext.CustomerMemberships
            .AnyAsync(membership => membership.Id == membershipId && membership.CustomerId == customerId, cancellationToken);
        if (!owned)
        {
            throw new NotFoundException(NotFoundMessage);
        }

        var detail = await _memberships.GetByIdAsync(membershipId, cancellationToken);
        return new MyMembershipView(
            detail.Id,
            detail.PlanName,
            detail.PlanDescription,
            detail.Price,
            detail.Mode,
            detail.DisplayStatus,
            detail.SoldOn,
            detail.StartsOn,
            detail.EndsOn,
            // Moved here by a merge of the user's duplicate profiles: show who it was bought as.
            detail.OriginalCustomerId != detail.CustomerId ? detail.OriginalCustomerName : null,
            detail.Allowances
                .Select(line => new MyMembershipBalanceView(
                    line.Origin,
                    line.Services.Select(service => service.ServiceName).ToArray(),
                    line.GrantedQuantity,
                    line.ConsumedQuantity,
                    line.AvailableQuantity))
                .ToArray(),
            detail.Gifts
                .Select(gift => new MyMembershipGiftView(
                    gift.Kind,
                    (gift.Kind == "Service" ? gift.ServiceName : gift.Description) ?? string.Empty,
                    gift.Quantity,
                    gift.Status,
                    gift.DeliveredOn))
                .ToArray(),
            detail.Consumptions
                .Select(consumption => new MyMembershipConsumptionView(
                    consumption.ServiceName,
                    consumption.Quantity,
                    consumption.ServiceDate,
                    consumption.Parts.Select(part => new MyMembershipConsumptionPartView(part.Origin, part.Quantity)).ToArray(),
                    consumption.Reversal is not null,
                    consumption.Reversal?.PerformedAt))
                .ToArray(),
            detail.Terms,
            detail.SpecificConditions);
    }

    /// <summary>The user's current customer profile: linked to the user and not merged into another one.</summary>
    private async Task<Guid?> FindLinkedCustomerIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await _dbContext.Customers
            .Where(customer => customer.UserId == userId && customer.MergedIntoCustomerId == null)
            .Select(customer => (Guid?)customer.Id)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
