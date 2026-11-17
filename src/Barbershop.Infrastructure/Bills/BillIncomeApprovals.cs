using Barbershop.Domain.Bills;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Bills;

/// <summary>
/// ADR-0020 P4: which incomes of bills were posted (or reversed) by an approval an Admin made for themselves. Read from
/// the postings of each approval, not from the bill, whose current approval changes when it is unapproved and approved
/// again: an original income and its counter-entry carry the mark of the same approval, so they cancel out.
/// </summary>
internal static class BillIncomeApprovals
{
    public static async Task<HashSet<Guid>> SelfApprovedIncomeIdsAsync(
        AppDbContext dbContext, IReadOnlyCollection<Guid> incomeIds, CancellationToken cancellationToken)
    {
        if (incomeIds.Count == 0)
        {
            return [];
        }

        var ids = incomeIds.Select(id => (Guid?)id).ToArray();
        var rows = await (
                from posting in dbContext.BillPostings.AsNoTracking()
                join approval in dbContext.BillApprovals.AsNoTracking() on posting.ApprovalId equals approval.Id
                where approval.IsSelfApproved && posting.Effect == BillPostingEffect.Income
                    && (ids.Contains(posting.IncomeEntryId) || ids.Contains(posting.ReversalTargetId))
                select new { posting.IncomeEntryId, posting.ReversalTargetId })
            .ToListAsync(cancellationToken);
        return rows.SelectMany(row => new[] { row.IncomeEntryId, row.ReversalTargetId })
            .OfType<Guid>()
            .ToHashSet();
    }
}
