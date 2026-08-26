using Barbershop.Application.Auth;
using Barbershop.Application.Users.Admin;

namespace Api.Barbershop.Features.Admin.Users;

public static class AdminUsersEndpoints
{
    public static RouteGroupBuilder MapAdminUsersEndpoints(this RouteGroupBuilder api)
    {
        var adminUsers = api.MapGroup("/admin/users")
            .WithTags("Admin")
            .RequireAuthorization(AuthPolicyNames.Admin);

        adminUsers.MapGet(string.Empty, GetAllAsync)
            .WithName("GetAdminUserList")
            .Produces<IReadOnlyList<AdminUserListItem>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminUsers.MapGet("/{userId:guid}", GetByIdAsync)
            .WithName("GetAdminUserById")
            .Produces<AdminUserListItem>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminUsers.MapPut("/{userId:guid}", UpdateAsync)
            .WithName("UpdateAdminUser")
            .Produces<AdminUserListItem>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminUsers.MapDelete("/{userId:guid}", DeactivateAsync)
            .WithName("DeactivateAdminUser")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminUsers.MapPatch("/{userId:guid}/roles", UpdateCustomRolesAsync)
            .WithName("UpdateAdminUserCustomRoles")
            .Produces<AdminUserListItem>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static Task<IReadOnlyList<AdminUserListItem>> GetAllAsync(
        IAdminUsersService service, CancellationToken cancellationToken)
        => service.GetAllActiveAsync(cancellationToken);

    private static Task<AdminUserListItem> GetByIdAsync(
        Guid userId, IAdminUsersService service, CancellationToken cancellationToken)
        => service.GetByIdAsync(userId, cancellationToken);

    private static Task<AdminUserListItem> UpdateAsync(
        Guid userId, AdminUserUpdateRequest request, IAdminUsersService service, CancellationToken cancellationToken)
        => service.UpdateAsync(userId, request, cancellationToken);

    private static async Task<IResult> DeactivateAsync(
        Guid userId, IAdminUsersService service, CancellationToken cancellationToken)
    {
        await service.DeactivateAsync(userId, cancellationToken);
        return Results.NoContent();
    }

    private static Task<AdminUserListItem> UpdateCustomRolesAsync(
        Guid userId, AdminUserRolesUpdateRequest request, IAdminUsersService service, CancellationToken cancellationToken)
        => service.UpdateCustomRolesAsync(userId, request, cancellationToken);
}
