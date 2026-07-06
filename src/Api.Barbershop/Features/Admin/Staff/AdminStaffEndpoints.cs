using System.Security.Claims;
using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Barbershop.Application.Staff;
using Barbershop.Application.Staff.Admin;

namespace Api.Barbershop.Features.Admin.Staff;

public static class AdminStaffEndpoints
{
    public static RouteGroupBuilder MapAdminStaffEndpoints(this RouteGroupBuilder api)
    {
        var adminStaff = api.MapGroup("/admin/staff")
            .WithTags("Admin")
            .RequireAuthorization(AuthPolicyNames.Admin);

        adminStaff.MapGet(string.Empty, GetAllAsync)
            .WithName("GetAdminStaffList")
            .Produces<IReadOnlyList<StaffManagementView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminStaff.MapGet("/{staffId:guid}", GetByIdAsync)
            .WithName("GetAdminStaffById")
            .Produces<StaffManagementView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminStaff.MapPost(string.Empty, CreateAsync)
            .WithName("CreateAdminStaff")
            .Produces<StaffManagementView>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminStaff.MapPut("/{staffId:guid}", UpdateAsync)
            .WithName("UpdateAdminStaff")
            .Produces<StaffManagementView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminStaff.MapPatch("/{staffId:guid}/status", UpdateStatusAsync)
            .WithName("UpdateAdminStaffStatus")
            .Produces<StaffManagementView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminStaff.MapPost("/me", EnableProfessionalForCurrentUserAsync)
            .WithName("EnableAdminProfessionalProfile")
            .Produces<StaffManagementView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static Task<IReadOnlyList<StaffManagementView>> GetAllAsync(IAdminStaffService service, CancellationToken cancellationToken)
        => service.GetAllAsync(cancellationToken);

    private static Task<StaffManagementView> GetByIdAsync(Guid staffId, IAdminStaffService service, CancellationToken cancellationToken)
        => service.GetByIdAsync(staffId, cancellationToken);

    private static async Task<IResult> CreateAsync(AdminStaffCreateRequest request, IAdminStaffService service, CancellationToken cancellationToken)
    {
        var response = await service.CreateAsync(request, cancellationToken);
        return Results.Created($"/api/v1/admin/staff/{response.StaffProfileId}", response);
    }

    private static Task<StaffManagementView> UpdateAsync(Guid staffId, AdminStaffUpdateRequest request, IAdminStaffService service, CancellationToken cancellationToken)
        => service.UpdateAsync(staffId, request, cancellationToken);

    private static Task<StaffManagementView> UpdateStatusAsync(Guid staffId, StaffStatusUpdateRequest request, IAdminStaffService service, CancellationToken cancellationToken)
        => service.UpdateStatusAsync(staffId, request, cancellationToken);

    private static Task<StaffManagementView> EnableProfessionalForCurrentUserAsync(
        EnableProfessionalProfileRequest request,
        ClaimsPrincipal user,
        IAdminStaffService service,
        CancellationToken cancellationToken)
        => service.EnableProfessionalForCurrentUserAsync(user.GetRequiredUserId(), request, cancellationToken);
}
