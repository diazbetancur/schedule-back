using Barbershop.Application.Auth;
using Barbershop.Application.Services.Admin;

namespace Api.Barbershop.Features.Admin.Services;

public static class AdminServicesEndpoints
{
    public static RouteGroupBuilder MapAdminServicesEndpoints(this RouteGroupBuilder api)
    {
        var adminServices = api.MapGroup("/admin/services")
            .WithTags("Admin")
            .RequireAuthorization(AuthPolicyNames.Admin);

        adminServices.MapGet(string.Empty, GetAllAsync)
            .WithName("GetAdminServices")
            .Produces<IReadOnlyList<ServiceView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminServices.MapGet("/{serviceId:guid}", GetByIdAsync)
            .WithName("GetAdminServiceById")
            .Produces<ServiceView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminServices.MapPost(string.Empty, CreateAsync)
            .WithName("CreateAdminService")
            .Produces<ServiceView>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminServices.MapPut("/{serviceId:guid}", UpdateAsync)
            .WithName("UpdateAdminService")
            .Produces<ServiceView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminServices.MapPatch("/{serviceId:guid}/status", UpdateStatusAsync)
            .WithName("UpdateAdminServiceStatus")
            .Produces<ServiceView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminServices.MapDelete("/{serviceId:guid}", SoftDeleteAsync)
            .WithName("DeleteAdminService")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static Task<IReadOnlyList<ServiceView>> GetAllAsync(IAdminServicesService service, CancellationToken cancellationToken)
        => service.GetAllAsync(cancellationToken);

    private static Task<ServiceView> GetByIdAsync(Guid serviceId, IAdminServicesService service, CancellationToken cancellationToken)
        => service.GetByIdAsync(serviceId, cancellationToken);

    private static async Task<IResult> CreateAsync(ServiceCreateRequest request, IAdminServicesService service, CancellationToken cancellationToken)
    {
        var response = await service.CreateAsync(request, cancellationToken);
        return Results.Created($"/api/v1/admin/services/{response.Id}", response);
    }

    private static Task<ServiceView> UpdateAsync(Guid serviceId, ServiceUpdateRequest request, IAdminServicesService service, CancellationToken cancellationToken)
        => service.UpdateAsync(serviceId, request, cancellationToken);

    private static Task<ServiceView> UpdateStatusAsync(Guid serviceId, ServiceStatusUpdateRequest request, IAdminServicesService service, CancellationToken cancellationToken)
        => service.UpdateStatusAsync(serviceId, request, cancellationToken);

    private static async Task<IResult> SoftDeleteAsync(Guid serviceId, IAdminServicesService service, CancellationToken cancellationToken)
    {
        await service.SoftDeleteAsync(serviceId, cancellationToken);
        return Results.NoContent();
    }
}
