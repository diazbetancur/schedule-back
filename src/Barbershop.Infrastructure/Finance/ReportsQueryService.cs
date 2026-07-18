using System.Globalization;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Finance.Admin;
using Barbershop.Domain.Finance;
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
                ["range"] = ["'from' must be on or before 'to'."],
            });
        }

        var incomes = await _dbContext.IncomeEntries
            .Where(entry => !entry.IsDeleted && entry.OccurredOn >= rangeFrom && entry.OccurredOn <= rangeTo)
            .ToListAsync(cancellationToken);

        var expenses = await _dbContext.ExpenseEntries
            .Where(entry => !entry.IsDeleted && entry.OccurredOn >= rangeFrom && entry.OccurredOn <= rangeTo)
            .ToListAsync(cancellationToken);

        var totalIncome = incomes.Sum(entry => (long)entry.Amount);
        var promoIncome = incomes.Where(entry => entry.IsPromo).Sum(entry => (long)entry.Amount);
        var businessIncome = incomes.Sum(entry => (long)entry.BusinessAmount);
        var professionalIncome = incomes.Sum(entry => (long)entry.ProfessionalAmount);
        var totalExpenses = expenses.Sum(entry => (long)entry.Amount);

        var staffIds = incomes.Select(entry => entry.StaffProfileId).Distinct().ToArray();
        var names = staffIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await _dbContext.StaffProfiles
                .Where(staff => staffIds.Contains(staff.Id))
                .Select(staff => new { staff.Id, staff.DisplayName })
                .ToDictionaryAsync(staff => staff.Id, staff => staff.DisplayName, cancellationToken);

        var byProfessional = incomes
            .GroupBy(entry => entry.StaffProfileId)
            .Select(group => new ReportProfessionalIncomeView(
                group.Key,
                names.TryGetValue(group.Key, out var name) ? name : string.Empty,
                group.Sum(entry => (long)entry.Amount),
                group.Count(),
                group.Sum(entry => (long)entry.BusinessAmount),
                group.Sum(entry => (long)entry.ProfessionalAmount)))
            .OrderByDescending(view => view.IncomeTotal)
            .ToArray();

        var byExpenseConcept = expenses
            .GroupBy(entry => entry.Name)
            .Select(group => new ReportExpenseConceptView(group.Key, group.Sum(entry => (long)entry.Amount)))
            .OrderByDescending(view => view.Total)
            .ToArray();

        var trend = BuildTrend(rangeFrom, rangeTo, incomes, expenses);

        return new ReportSummaryView(
            rangeFrom,
            rangeTo,
            totalIncome,
            totalExpenses,
            totalIncome - totalExpenses,
            incomes.Count,
            promoIncome,
            totalIncome - promoIncome,
            businessIncome,
            professionalIncome,
            byProfessional,
            byExpenseConcept,
            trend);
    }

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
