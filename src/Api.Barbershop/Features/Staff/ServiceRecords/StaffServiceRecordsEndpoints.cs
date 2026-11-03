using System.Security.Claims;
using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Barbershop.Application.ServiceRecords;

namespace Api.Barbershop.Features.Staff.ServiceRecords;

public static class StaffServiceRecordsEndpoints
{
    /// <summary>
    /// A professional registers the services they performed. Any user with service-records.register and a professional
    /// profile can use it, always for their own profile and their own appointments.
    /// </summary>
    public static RouteGroupBuilder MapStaffServiceRecordsEndpoints(this RouteGroupBuilder api)
    {
        var serviceRecords = api.MapGroup("/staff/service-records")
            .WithTags("Staff")
            .RequireAuthorization(PermissionPolicyNames.ServiceRecordsRegister);

        serviceRecords.MapGet("/catalog", GetCatalogAsync)
            .WithName("GetStaffServiceRecordsCatalog")
            .Produces<IReadOnlyList<ServiceRecordCatalogItem>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        serviceRecords.MapGet("/eligibility", GetEligibilityAsync)
            .WithName("GetStaffServiceRecordsEligibility")
            .Produces<ServiceRecordEligibilityView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        serviceRecords.MapGet("/appointments", GetAppointmentsAsync)
            .WithName("GetStaffServiceRecordsAppointments")
            .Produces<IReadOnlyList<ServiceRecordAppointmentOption>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        serviceRecords.MapGet(string.Empty, GetMineAsync)
            .WithName("GetStaffServiceRecords")
            .Produces<IReadOnlyList<ServiceRecordView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        serviceRecords.MapPost(string.Empty, RegisterAsync)
            .WithName("RegisterStaffServiceRecords")
            .Produces<IReadOnlyList<ServiceRecordView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static Task<IReadOnlyList<ServiceRecordCatalogItem>> GetCatalogAsync(
        IStaffServiceRecordsService service, CancellationToken cancellationToken)
        => service.GetCatalogAsync(cancellationToken);

    // Not role based: any user with the permission and an active professional profile of their own may register.
    private static Task<ServiceRecordEligibilityView> GetEligibilityAsync(
        ClaimsPrincipal user, IStaffServiceRecordsService service, CancellationToken cancellationToken)
        => service.GetEligibilityAsync(user.GetRequiredUserId(), cancellationToken);

    private static Task<IReadOnlyList<ServiceRecordAppointmentOption>> GetAppointmentsAsync(
        ClaimsPrincipal user, IStaffServiceRecordsService service, CancellationToken cancellationToken)
        => service.GetAppointmentsAsync(user.GetRequiredUserId(), cancellationToken);

    private static Task<IReadOnlyList<ServiceRecordView>> GetMineAsync(
        ClaimsPrincipal user, IStaffServiceRecordsService service, CancellationToken cancellationToken)
        => service.GetMineAsync(user.GetRequiredUserId(), cancellationToken);

    // 200 rather than 201: a retried registration returns the records already stored.
    private static Task<IReadOnlyList<ServiceRecordView>> RegisterAsync(
        ServiceRecordRegisterRequest request, ClaimsPrincipal user, IStaffServiceRecordsService service, CancellationToken cancellationToken)
        => service.RegisterAsync(user.GetRequiredUserId(), request, cancellationToken);
}
