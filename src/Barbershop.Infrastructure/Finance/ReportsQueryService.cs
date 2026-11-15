using System.Globalization;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Finance.Admin;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Bills;
using Barbershop.Domain.ServiceRecords;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Finance;

internal sealed class ReportsQueryService : IAdminReportsService
{
    private const int DailyBucketMaxSpanDays = 62;

    // Colombia is a fixed UTC-5 offset (no DST).
    private static readonly TimeSpan ColombiaOffset = TimeSpan.FromHours(-5);

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public ReportsQueryService(AppDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<ReportSummaryView> GetSummaryAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime.Add(ColombiaOffset));
        var firstOfMonth = new DateOnly(today.Year, today.Month, 1);
        var rangeFrom = from ?? firstOfMonth;
        var rangeTo = to ?? firstOfMonth.AddMonths(1).AddDays(-1);

        if (rangeFrom > rangeTo)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["range"] = ["La fecha inicial no puede ser posterior a la final."],
            });
        }

        var incomes = await _dbContext.IncomeEntries
            .Where(entry => !entry.IsDeleted && entry.OccurredOn >= rangeFrom && entry.OccurredOn <= rangeTo)
            .ToListAsync(cancellationToken);

        var expenses = await _dbContext.ExpenseEntries
            .Where(entry => !entry.IsDeleted && entry.OccurredOn >= rangeFrom && entry.OccurredOn <= rangeTo)
            .ToListAsync(cancellationToken);

        // Membership sales are money in (total, business share, trend) but not services performed: they stay
        // out of the service count, the promo/normal split and the per-professional table. Every historical
        // entry is a service entry, so reports over past periods are unchanged.
        var serviceIncomes = incomes.Where(entry => entry.Kind == IncomeKind.Service).ToList();
        var membershipSales = incomes.Where(entry => entry.Kind == IncomeKind.MembershipSale).ToList();
        // ADR-0020: products and tips come only from approved bills; they are money in, not services performed.
        var productSales = incomes.Where(entry => entry.Kind == IncomeKind.ProductSale).ToList();
        var tips = incomes.Where(entry => entry.Kind == IncomeKind.Tip).ToList();

        var totalIncome = incomes.Sum(entry => (long)entry.Amount);
        var serviceIncome = serviceIncomes.Sum(entry => (long)entry.Amount);
        var promoIncome = serviceIncomes.Where(entry => entry.IsPromo).Sum(entry => (long)entry.Amount);
        var membershipSalesIncome = membershipSales.Sum(entry => (long)entry.Amount);
        var businessIncome = incomes.Sum(entry => (long)entry.BusinessAmount);
        var professionalIncome = incomes.Sum(entry => (long)entry.ProfessionalAmount);
        var totalExpenses = expenses.Sum(entry => (long)entry.Amount);

        var staffIds = serviceIncomes.Select(entry => entry.StaffProfileId!.Value).Distinct().ToArray();
        var names = staffIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await _dbContext.StaffProfiles
                .Where(staff => staffIds.Contains(staff.Id))
                .Select(staff => new { staff.Id, staff.DisplayName })
                .ToDictionaryAsync(staff => staff.Id, staff => staff.DisplayName, cancellationToken);

        var tipStaffIds = tips.Select(entry => entry.StaffProfileId!.Value).Where(id => !names.ContainsKey(id)).Distinct().ToArray();
        foreach (var staff in tipStaffIds.Length == 0
                     ? []
                     : await _dbContext.StaffProfiles.AsNoTracking()
                         .Where(staff => tipStaffIds.Contains(staff.Id))
                         .Select(staff => new { staff.Id, staff.DisplayName })
                         .ToListAsync(cancellationToken))
        {
            names[staff.Id] = staff.DisplayName;
        }

        var tipsByProfessional = tips.GroupBy(entry => entry.StaffProfileId!.Value)
            .ToDictionary(group => group.Key, group => group.Sum(entry => (long)entry.Amount));
        var byProfessional = serviceIncomes
            .GroupBy(entry => entry.StaffProfileId!.Value)
            .Select(group => new ReportProfessionalIncomeView(
                group.Key,
                names.TryGetValue(group.Key, out var name) ? name : string.Empty,
                group.Sum(entry => (long)entry.Amount),
                group.Sum(Count),
                group.Sum(entry => (long)entry.BusinessAmount),
                group.Sum(entry => (long)entry.ProfessionalAmount),
                tipsByProfessional.GetValueOrDefault(group.Key)))
            .Concat(tipsByProfessional.Keys
                .Where(staffId => serviceIncomes.All(entry => entry.StaffProfileId != staffId))
                .Select(staffId => new ReportProfessionalIncomeView(
                    staffId, names.GetValueOrDefault(staffId, string.Empty), 0, 0, 0, 0, tipsByProfessional[staffId])))
            .OrderByDescending(view => view.IncomeTotal).ThenByDescending(view => view.TipTotal)
            .ToArray();

        var byExpenseConcept = expenses
            .GroupBy(entry => entry.Name)
            .Select(group => new ReportExpenseConceptView(group.Key, group.Sum(entry => (long)entry.Amount)))
            .OrderByDescending(view => view.Total)
            .ToArray();

        var trend = BuildTrend(rangeFrom, rangeTo, incomes, expenses);

        // Informative block (ADR-0015 R14): commissions the business covers for rewards delivered as services, from
        // the snapshots of the confirmed records. They are not payments and do not enter any total above.
        var rewardRecords = await _dbContext.ServiceRecords.AsNoTracking()
            .Where(record => record.Status == ServiceRecordStatus.Approved
                && record.OperationType == ServiceRecordOperationType.LoyaltyReward
                && record.ApprovedPerformedOn >= rangeFrom && record.ApprovedPerformedOn <= rangeTo)
            .Select(record => new
            {
                StaffProfileId = record.ApprovedStaffProfileId!.Value,
                BasePrice = record.RewardBasePriceSnapshot!.Value,
                Commission = record.RewardProfessionalCommission!.Value
            })
            .ToListAsync(cancellationToken);
        // ADR-0020: service rewards delivered by approved bills, from the snapshots copied on their lines.
        var rewardLines = await (
                from line in _dbContext.BillLines.AsNoTracking()
                join bill in _dbContext.Bills.AsNoTracking() on line.BillId equals bill.Id
                where bill.Status == BillStatus.Approved && line.Type == BillLineType.Redemption && line.ServiceId != null
                    && bill.ServiceDate >= rangeFrom && bill.ServiceDate <= rangeTo
                select new { StaffProfileId = line.StaffProfileId!.Value, BasePrice = line.UnitPrice, Percentage = line.BusinessPercentageSnapshot!.Value })
            .ToListAsync(cancellationToken);
        rewardRecords.AddRange(rewardLines.Select(line => new
        {
            line.StaffProfileId,
            line.BasePrice,
            Commission = line.BasePrice - IncomeEntry.BusinessShare(line.BasePrice, line.Percentage)
        }));

        var rewardStaffIds = rewardRecords.Select(record => record.StaffProfileId).Distinct().Where(id => !names.ContainsKey(id)).ToArray();
        if (rewardStaffIds.Length > 0)
        {
            foreach (var staff in await _dbContext.StaffProfiles.AsNoTracking()
                         .Where(staff => rewardStaffIds.Contains(staff.Id))
                         .Select(staff => new { staff.Id, staff.DisplayName })
                         .ToListAsync(cancellationToken))
            {
                names[staff.Id] = staff.DisplayName;
            }
        }

        var rewardCommissions = rewardRecords
            .GroupBy(record => record.StaffProfileId)
            .Select(group => new ReportRewardCommissionView(
                group.Key,
                names.TryGetValue(group.Key, out var name) ? name : string.Empty,
                group.Count(),
                group.Sum(record => (long)record.BasePrice),
                group.Sum(record => (long)(record.BasePrice - record.Commission)),
                group.Sum(record => (long)record.Commission)))
            .OrderByDescending(view => view.ProfessionalCommissionTotal)
            .ToArray();

        // ADR-0018 D1 (SONDA BUG-090): incomes of service records an Admin approved for themselves, shown apart.
        var incomeIds = incomes.Select(entry => (Guid?)entry.Id).ToArray();
        var selfApprovedIncomeIds = await _dbContext.ServiceRecords.AsNoTracking()
            .Where(record => record.IsSelfApproved && incomeIds.Contains(record.IncomeEntryId))
            .Select(record => record.IncomeEntryId!.Value)
            .ToListAsync(cancellationToken);
        // ADR-0020 P4: incomes (and counter-entries) of bill approvals an Admin made for themselves.
        var selfApprovedBillIncomes = await Barbershop.Infrastructure.Bills.BillIncomeApprovals.SelfApprovedIncomeIdsAsync(
            _dbContext, incomes.Where(entry => entry.BillId is not null).Select(entry => entry.Id).ToArray(), cancellationToken);
        var selfApprovedIncomes = incomes
            .Where(entry => selfApprovedIncomeIds.Contains(entry.Id) || selfApprovedBillIncomes.Contains(entry.Id))
            .ToList();

        return new ReportSummaryView(
            rangeFrom,
            rangeTo,
            totalIncome,
            totalExpenses,
            totalIncome - totalExpenses,
            serviceIncomes.Sum(Count),
            promoIncome,
            serviceIncome - promoIncome,
            businessIncome,
            professionalIncome,
            byProfessional,
            byExpenseConcept,
            trend,
            membershipSalesIncome,
            membershipSales.Count,
            rewardCommissions,
            rewardCommissions.Sum(view => view.ProfessionalCommissionTotal),
            selfApprovedIncomes.Sum(entry => (long)entry.Amount),
            selfApprovedIncomes.Sum(Count),
            productSales.Sum(entry => (long)entry.Amount),
            productSales.Sum(entry => (entry.Quantity ?? 0) * Count(entry)),
            tips.Sum(entry => (long)entry.Amount),
            tips.Sum(Count),
            incomes.Where(entry => entry.CommissionBaseAmount is not null).Sum(entry => (long)(entry.CommissionBaseAmount!.Value - entry.Amount)));
    }

    /// <summary>ADR-0020 Q8: a counter-entry takes back the service, unit or tip its original counted.</summary>
    private static int Count(IncomeEntry entry) => entry.IsReversal ? -1 : 1;

    private static IReadOnlyList<ReportTrendPointView> BuildTrend(
        DateOnly rangeFrom,
        DateOnly rangeTo,
        IReadOnlyList<IncomeEntry> incomes,
        IReadOnlyList<ExpenseEntry> expenses)
    {
        var spanDays = rangeTo.DayNumber - rangeFrom.DayNumber + 1;
        var points = new List<ReportTrendPointView>();

        if (spanDays <= DailyBucketMaxSpanDays)
        {
            for (var day = rangeFrom; day <= rangeTo; day = day.AddDays(1))
            {
                var current = day;
                var income = incomes.Where(entry => entry.OccurredOn == current).Sum(entry => (long)entry.Amount);
                var expense = expenses.Where(entry => entry.OccurredOn == current).Sum(entry => (long)entry.Amount);
                points.Add(new ReportTrendPointView(
                    current.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), current, income, expense));
            }

            return points;
        }

        var monthStart = new DateOnly(rangeFrom.Year, rangeFrom.Month, 1);
        var lastMonthStart = new DateOnly(rangeTo.Year, rangeTo.Month, 1);
        for (var month = monthStart; month <= lastMonthStart; month = month.AddMonths(1))
        {
            var start = month;
            var end = month.AddMonths(1);
            var income = incomes.Where(entry => entry.OccurredOn >= start && entry.OccurredOn < end).Sum(entry => (long)entry.Amount);
            var expense = expenses.Where(entry => entry.OccurredOn >= start && entry.OccurredOn < end).Sum(entry => (long)entry.Amount);
            points.Add(new ReportTrendPointView(
                start.ToString("yyyy-MM", CultureInfo.InvariantCulture), start, income, expense));
        }

        return points;
    }
}
