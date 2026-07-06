using Barbershop.Application.Auth;
using Barbershop.Application.Finance.Admin;

namespace Api.Barbershop.Features.Admin.Finance;

public static class AdminReportsEndpoints
{
    public static RouteGroupBuilder MapAdminReportsEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/admin/reports")
            .WithTags("Admin")
            .RequireAuthorization(AuthPolicyNames.Admin);

        group.MapGet("/summary", GetSummaryAsync)
            .WithName("GetAdminReportsSummary")
            .Produces<ReportSummaryView>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static Task<ReportSummaryView> GetSummaryAsync(
        DateOnly? from,
        DateOnly? to,
        IAdminReportsService service,
        CancellationToken cancellationToken)
        => service.GetSummaryAsync(from, to, cancellationToken);
}
