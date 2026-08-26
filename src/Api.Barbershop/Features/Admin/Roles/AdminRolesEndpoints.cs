using Barbershop.Application.Auth;
using Barbershop.Application.Authorization;

namespace Api.Barbershop.Features.Admin.Roles;

public static class AdminRolesEndpoints
{
    public static RouteGroupBuilder MapAdminRolesEndpoints(this RouteGroupBuilder api)
    {
        var adminRoles = api.MapGroup("/admin/roles")
            .WithTags("Admin")
            .RequireAuthorization(AuthPolicyNames.Admin);

        adminRoles.MapGet(string.Empty, GetAllAsync)
            .WithName("GetAdminRoles")
            .Produces<IReadOnlyList<RoleView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminRoles.MapPost(string.Empty, CreateAsync)
            .WithName("CreateAdminRole")
            .Produces<RoleView>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminRoles.MapPut("/{roleId:guid}", UpdateAsync)
            .WithName("UpdateAdminRole")
            .Produces<RoleView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminRoles.MapDelete("/{roleId:guid}", DeleteAsync)
            .WithName("DeleteAdminRole")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        api.MapGroup("/admin/permissions")
            .WithTags("Admin")
            .RequireAuthorization(AuthPolicyNames.Admin)
            .MapGet(string.Empty, GetAllPermissionsAsync)
            .WithName("GetAdminPermissions")
            .Produces<IReadOnlyList<PermissionView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static Task<IReadOnlyList<RoleView>> GetAllAsync(IAdminRolesService service, CancellationToken cancellationToken)
        => service.GetAllAsync(cancellationToken);

    private static async Task<IResult> CreateAsync(RoleCreateRequest request, IAdminRolesService service, CancellationToken cancellationToken)
    {
        var response = await service.CreateAsync(request, cancellationToken);
        return Results.Created($"/api/v1/admin/roles/{response.Id}", response);
    }

    private static Task<RoleView> UpdateAsync(Guid roleId, RoleUpdateRequest request, IAdminRolesService service, CancellationToken cancellationToken)
        => service.UpdateAsync(roleId, request, cancellationToken);

    private static async Task<IResult> DeleteAsync(Guid roleId, IAdminRolesService service, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(roleId, cancellationToken);
        return Results.NoContent();
    }

    private static Task<IReadOnlyList<PermissionView>> GetAllPermissionsAsync(IAdminRolesService service, CancellationToken cancellationToken)
        => service.GetAllPermissionsAsync(cancellationToken);
}
