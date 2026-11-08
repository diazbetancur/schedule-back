namespace Barbershop.Application.Finance.Admin;

public sealed record ReportProfessionalIncomeView(
    Guid StaffProfileId,
    string DisplayName,
    long IncomeTotal,
    int IncomeCount,
    long BusinessTotal,
    long ProfessionalTotal);

public sealed record ReportExpenseConceptView(string Name, long Total);

public sealed record ReportTrendPointView(string Bucket, DateOnly From, long Income, long Expenses);

/// <summary>
/// <c>TotalIncome</c>, <c>BusinessIncome</c> and <c>Trend</c> include membership sales. <c>IncomeCount</c>
/// (services performed), <c>PromoIncome</c>/<c>NormalIncome</c> and <c>ByProfessional</c> cover service income
/// only, so PromoIncome + NormalIncome + MembershipSalesIncome = TotalIncome.
/// </summary>
public sealed record ReportSummaryView(
    DateOnly From,
    DateOnly To,
    long TotalIncome,
    long TotalExpenses,
    long NetProfit,
    int IncomeCount,
    long PromoIncome,
    long NormalIncome,
    long BusinessIncome,
    long ProfessionalIncome,
    IReadOnlyList<ReportProfessionalIncomeView> ByProfessional,
    IReadOnlyList<ReportExpenseConceptView> ByExpenseConcept,
    IReadOnlyList<ReportTrendPointView> Trend,
    long MembershipSalesIncome,
    int MembershipSalesCount,
    IReadOnlyList<ReportRewardCommissionView>? RewardCommissions = null,
    long RewardCommissionsTotal = 0);

/// <summary>
/// Informative (ADR-0015 R14): commissions the business covers for loyalty rewards delivered as services, by
/// professional, from the snapshots of the confirmed records. Not a payment made, and not part of any other total.
/// </summary>
public sealed record ReportRewardCommissionView(
    Guid StaffProfileId,
    string StaffName,
    int RewardCount,
    long BasePriceTotal,
    long BusinessShareTotal,
    long ProfessionalCommissionTotal);
