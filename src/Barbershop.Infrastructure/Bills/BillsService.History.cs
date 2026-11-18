using Barbershop.Application.Bills;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Common.Paging;
using Barbershop.Domain.Bills;
using Barbershop.Domain.Common;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Bills;

/// <summary>
/// ADR-0020 stage 5: the receipts of the customer (P8, Q23) and the cash closing by day and payment method. The customer
/// sees the bills of their profile and of every profile merged into it (ADR-0013), once they have a number: never the
/// internal states, the commissions or the costs; a voided one as «Anulado» and one being corrected as «En revisión».
/// </summary>
internal sealed partial class BillsService : IMyBillsService, IBillReportsService
{
    private const string MyBillNotFoundMessage = "El recibo de caja no existe.";

    public async Task<PagedResult<MyBillSummaryView>> GetMineAsync(Guid currentUserId, MyBillListQuery query, CancellationToken cancellationToken = default)
    {
        var page = PageRequest.Create(query.Page, query.PageSize);
        var family = await OwnProfileFamilyAsync(currentUserId, cancellationToken);
        if (family.Length == 0)
        {
            return new PagedResult<MyBillSummaryView>([], page.Page, page.PageSize, 0);
        }

        return await _dbContext.Bills.AsNoTracking()
            .Where(bill => bill.CustomerId != null && family.Contains(bill.CustomerId.Value) && bill.Number != null)
            .OrderByDescending(bill => bill.ClosedAt).ThenBy(bill => bill.Id)
            .ToPagedResultAsync(
                page,
                bill => new MyBillSummaryView(
                    bill.Id, Bill.FormatNumber(bill.Series!, bill.Number!.Value), CustomerStatus(bill.Status), bill.ServiceDate, bill.ClosedAt!.Value,
                    bill.Total, bill.PaymentMethodNameSnapshot),
                cancellationToken);
    }

    public async Task<MyBillView> GetMineByIdAsync(Guid currentUserId, Guid billId, CancellationToken cancellationToken = default)
    {
        var family = await OwnProfileFamilyAsync(currentUserId, cancellationToken);
        var bill = await _dbContext.Bills.AsNoTracking().Include(candidate => candidate.Lines).AsSplitQuery()
            .SingleOrDefaultAsync(candidate => candidate.Id == billId, cancellationToken);
        // Someone else's bill (or one without a number yet) answers like a missing one: its existence is not revealed.
        if (bill is null || bill.Number is null || bill.CustomerId is not { } owner || !family.Contains(owner))
        {
            throw new NotFoundException(MyBillNotFoundMessage);
        }

        var staffIds = bill.Lines.Where(line => line.StaffProfileId is not null).Select(line => line.StaffProfileId!.Value).Distinct().ToArray();
        var staffNames = await _dbContext.StaffProfiles.AsNoTracking()
            .Where(staff => staffIds.Contains(staff.Id))
            .ToDictionaryAsync(staff => staff.Id, staff => staff.DisplayName, cancellationToken);
        var lines = bill.Lines.OrderBy(line => line.Position)
            .Select(line => new MyBillLineView(
                line.Type.ToString(),
                line.NameSnapshot,
                line.Quantity,
                line.UnitPrice,
                line.Amount,
                line.IsCourtesy,
                line.StaffProfileId is { } staffId ? staffNames.GetValueOrDefault(staffId) : null,
                line.TipPaymentMode?.ToString()))
            .ToArray();
        return new MyBillView(
            bill.Id, bill.DisplayNumber!, CustomerStatus(bill.Status), bill.ServiceDate, bill.ClosedAt!.Value, bill.CustomerNameSnapshot, lines,
            bill.Subtotal, bill.DiscountTotal, bill.TipTotal, bill.DirectTipTotal, bill.Total, bill.PaymentMethodNameSnapshot);
    }

    public async Task<CashClosingView> GetCashClosingAsync(CashClosingQuery query, CancellationToken cancellationToken = default)
    {
        var (from, to) = HistoryRange.Resolve(query.From, query.To, Today());
        var start = BogotaClock.ToUtc(from, TimeOnly.MinValue);
        var end = BogotaClock.ToUtc(to.AddDays(1), TimeOnly.MinValue);
        // SONDA BUG-121: the cash closing counts what was charged when each bill was closed, not its current total or
        // payment method (an approver may correct both later). Bills closed before the copy existed and whose charge could
        // not be rebuilt without ambiguity (see AddBillChargeSnapshot) keep their current values.
        var closed = (await _dbContext.Bills.AsNoTracking()
                .Where(bill => bill.ClosedAt >= start && bill.ClosedAt < end)
                .Select(bill => new
                {
                    ClosedAt = bill.ClosedAt!.Value,
                    bill.Status,
                    Total = bill.ChargedTotal ?? bill.Total,
                    MethodId = bill.ChargedTotal != null ? bill.ChargedPaymentMethodId : bill.PaymentMethodId,
                    MethodName = bill.ChargedTotal != null ? bill.ChargedPaymentMethodName : bill.PaymentMethodNameSnapshot,
                })
                .ToListAsync(cancellationToken))
            .Where(bill => bill.Total > 0 && bill.MethodId != null)
            .Select(bill => new CashRow(bill.ClosedAt, bill.Status, bill.MethodId!.Value, bill.MethodName ?? string.Empty, bill.Total))
            .ToList();
        var charged = closed.Where(bill => bill.Status != BillStatus.Voided).ToList();
        var voided = closed.Where(bill => bill.Status == BillStatus.Voided).ToList();

        static IReadOnlyList<CashClosingMethodView> ByMethod(IEnumerable<CashRow> rows)
            => rows.GroupBy(row => row.PaymentMethodId)
                .Select(group => new CashClosingMethodView(
                    group.Key, group.First().PaymentMethodName, group.Count(), group.Sum(row => (long)row.Total)))
                .OrderByDescending(method => method.Total).ThenBy(method => method.Name, StringComparer.CurrentCulture)
                .ToArray();

        var days = charged
            .GroupBy(bill => DateOnly.FromDateTime(BogotaClock.ToLocal(bill.ClosedAt)))
            .OrderBy(group => group.Key)
            .Select(group => new CashClosingDayView(group.Key, ByMethod(group), group.Count(), group.Sum(bill => (long)bill.Total)))
            .ToArray();
        return new CashClosingView(
            from, to, days, ByMethod(charged), charged.Count, charged.Sum(bill => (long)bill.Total), voided.Count, voided.Sum(bill => (long)bill.Total));
    }

    private sealed record CashRow(DateTime ClosedAt, BillStatus Status, Guid PaymentMethodId, string PaymentMethodName, int Total);

    /// <summary>Q23: Issued (closed or approved), UnderReview (unapproved, being corrected) or Voided.</summary>
    private static string CustomerStatus(BillStatus status) => status switch
    {
        BillStatus.Voided => "Voided",
        BillStatus.Unapproved => "UnderReview",
        _ => "Issued"
    };

    /// <summary>The live profile linked to the account and every profile merged into it, at any depth (ADR-0013).</summary>
    private async Task<Guid[]> OwnProfileFamilyAsync(Guid currentUserId, CancellationToken cancellationToken)
    {
        var own = await _dbContext.Customers.AsNoTracking()
            .Where(customer => customer.UserId == currentUserId && customer.MergedIntoCustomerId == null)
            .Select(customer => (Guid?)customer.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return own is { } id ? await ProfileFamilyAsync(_dbContext, id, cancellationToken) : [];
    }

    internal static async Task<Guid[]> ProfileFamilyAsync(AppDbContext dbContext, Guid customerId, CancellationToken cancellationToken)
    {
        var all = new List<Guid> { customerId };
        var frontier = new List<Guid> { customerId };
        for (var depth = 0; depth < 32 && frontier.Count > 0; depth++)
        {
            var current = frontier.ToArray();
            frontier = (await dbContext.Customers.AsNoTracking()
                    .Where(customer => customer.MergedIntoCustomerId != null && current.Contains(customer.MergedIntoCustomerId.Value))
                    .Select(customer => customer.Id)
                    .ToListAsync(cancellationToken))
                .Except(all)
                .ToList();
            all.AddRange(frontier);
        }

        return all.ToArray();
    }
}
