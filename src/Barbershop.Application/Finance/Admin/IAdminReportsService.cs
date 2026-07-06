namespace Barbershop.Application.Finance.Admin;

public interface IAdminReportsService
{
    Task<ReportSummaryView> GetSummaryAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default);
}
