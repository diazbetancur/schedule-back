namespace Barbershop.Application.Finance.Admin;

public sealed record ReportProfessionalIncomeView(
    Guid StaffProfileId,
    string DisplayName,
    long IncomeTotal,
    int IncomeCount);

public sealed record ReportExpenseConceptView(string Name, long Total);

public sealed record ReportTrendPointView(string Bucket, DateOnly From, long Income, long Expenses);

public sealed record ReportSummaryView(
    DateOnly From,
    DateOnly To,
    long TotalIncome,
    long TotalExpenses,
    long NetProfit,
    int IncomeCount,
    long PromoIncome,
    long NormalIncome,
    IReadOnlyList<ReportProfessionalIncomeView> ByProfessional,
    IReadOnlyList<ReportExpenseConceptView> ByExpenseConcept,
    IReadOnlyList<ReportTrendPointView> Trend);
