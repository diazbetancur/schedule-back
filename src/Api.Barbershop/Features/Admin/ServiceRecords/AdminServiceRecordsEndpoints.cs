using System.Security.Claims;
using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Barbershop.Application.Common.Paging;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.ServiceRecords;
using Barbershop.Domain.ServiceRecords;
using Microsoft.AspNetCore.Authorization;

namespace Api.Barbershop.Features.Admin.ServiceRecords;

public static class AdminServiceRecordsEndpoints
{
    /// <summary>
    /// Review of registered services. service-records.approve is a critical permission: every request, reads included,
    /// is checked against the database, so a revocation applies to the next request with the same token. An
    /// administrator who is also a professional may approve their own records; both steps keep who did them.
    /// </summary>
    public static RouteGroupBuilder MapAdminServiceRecordsEndpoints(this RouteGroupBuilder api)
    {
        var serviceRecords = api.MapGroup("/admin/service-records")
            .WithTags("Admin")
            .RequireAuthorization(PermissionPolicyNames.ServiceRecordsApprove);

        serviceRecords.MapGet(string.Empty, ListAsync)
            .WithName("GetAdminServiceRecords")
            .Produces<PagedResult<ServiceRecordView>>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        serviceRecords.MapGet("/options", GetOptionsAsync)
            .WithName("GetAdminServiceRecordOptions")
            .Produces<ServiceRecordOptionsView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        serviceRecords.MapGet("/customer-options", SearchCustomersAsync)
            .WithName("GetAdminServiceRecordCustomerOptions")
            .Produces<IReadOnlyList<ServiceRecordCustomerOption>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // Customers can be created while confirming, with the same logic as /admin/customers (duplicate check
        // included), so approvers without the Admin role are not stuck. No account is required nor linked.
        serviceRecords.MapGet("/customer-duplicates", FindCustomerDuplicatesAsync)
            .WithName("GetAdminServiceRecordCustomerDuplicates")
            .Produces<IReadOnlyList<ServiceRecordCustomerDuplicate>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        serviceRecords.MapPost("/customers", CreateCustomerAsync)
            .WithName("CreateAdminServiceRecordCustomer")
            .Produces<ServiceRecordCustomerOption>(StatusCodes.Status201Created)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        serviceRecords.MapGet("/{serviceRecordId:guid}/related", GetRelatedAsync)
            .WithName("GetAdminServiceRecordRelated")
            .Produces<ServiceRecordRelatedView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        serviceRecords.MapGet("/redemption-options", GetRedemptionOptionsAsync)
            .WithName("GetAdminServiceRecordRedemptionOptions")
            .Produces<IReadOnlyList<ServiceRecordRedemptionOption>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        serviceRecords.MapGet("/{serviceRecordId:guid}", GetByIdAsync)
            .WithName("GetAdminServiceRecordById")
            .Produces<ServiceRecordView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        serviceRecords.MapPost("/{serviceRecordId:guid}/approve", ApproveAsync)
            .WithName("ApproveAdminServiceRecord")
            .Produces<ServiceRecordView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        serviceRecords.MapPost("/{serviceRecordId:guid}/reject", RejectAsync)
            .WithName("RejectAdminServiceRecord")
            .Produces<ServiceRecordView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static Task<PagedResult<ServiceRecordView>> ListAsync(
        [AsParameters] ServiceRecordListQuery query, IAdminServiceRecordsService service, CancellationToken cancellationToken)
        => service.ListAsync(query, cancellationToken);

    private static Task<ServiceRecordOptionsView> GetOptionsAsync(IAdminServiceRecordsService service, CancellationToken cancellationToken)
        => service.GetOptionsAsync(cancellationToken);

    private static Task<IReadOnlyList<ServiceRecordCustomerOption>> SearchCustomersAsync(
        string? q, IAdminServiceRecordsService service, CancellationToken cancellationToken)
        => service.SearchCustomersAsync(q, cancellationToken);

    // Same checks and creation as /admin/customers; the answer is reduced (ServiceRecordCustomerPrivacy) because
    // approvers may not have the Admin role.
    private static async Task<IReadOnlyList<ServiceRecordCustomerDuplicate>> FindCustomerDuplicatesAsync(
        string? email, string? phone, IAdminCustomersService customers, CancellationToken cancellationToken)
    {
        var candidates = await customers.FindDuplicatesAsync(email, phone, null, null, cancellationToken);
        return candidates.Select(ServiceRecordCustomerPrivacy.ToDuplicate).ToArray();
    }

    private static async Task<IResult> CreateCustomerAsync(
        CustomerCreateRequest request, ClaimsPrincipal user, IAdminCustomersService customers, CancellationToken cancellationToken)
    {
        var created = await customers.CreateAsync(user.GetRequiredUserId(), request, cancellationToken);
        var response = new ServiceRecordCustomerOption(
            created.Id, created.FullName, ServiceRecordCustomerPrivacy.PhoneHint(created.Phone), []);
        return Results.Created($"/api/v1/admin/service-records/customer-options?q={Uri.EscapeDataString(created.FullName)}", response);
    }

    private static Task<ServiceRecordRelatedView> GetRelatedAsync(
        Guid serviceRecordId, IAdminServiceRecordsService service, CancellationToken cancellationToken)
        => service.GetRelatedAsync(serviceRecordId, cancellationToken);

    private static Task<ServiceRecordView> GetByIdAsync(
        Guid serviceRecordId, IAdminServiceRecordsService service, CancellationToken cancellationToken)
        => service.GetByIdAsync(serviceRecordId, cancellationToken);

    private static Task<IReadOnlyList<ServiceRecordRedemptionOption>> GetRedemptionOptionsAsync(
        Guid customerId, Guid? serviceId, IAdminServiceRecordsService service, CancellationToken cancellationToken)
        => service.GetRedemptionOptionsAsync(customerId, serviceId, cancellationToken);

    /// <summary>
    /// Confirming a record as a loyalty reward delivers a redemption, so it also needs the critical
    /// loyalty.redemptions.deliver permission, checked against the database (ADR-0015 R13).
    /// </summary>
    private static async Task<ServiceRecordView> ApproveAsync(
        Guid serviceRecordId, ServiceRecordApproveRequest request, ClaimsPrincipal user,
        IAdminServiceRecordsService service, IAuthorizationService authorization, CancellationToken cancellationToken)
    {
        if (string.Equals(request.OperationType?.Trim(), nameof(ServiceRecordOperationType.LoyaltyReward), StringComparison.OrdinalIgnoreCase)
            && !(await authorization.AuthorizeAsync(user, PermissionPolicyNames.LoyaltyRedemptionsDeliver)).Succeeded)
        {
            throw new UnauthorizedAccessException("No tienes permiso para entregar recompensas de fidelización.");
        }

        return await service.ApproveAsync(user.GetRequiredUserId(), serviceRecordId, request, cancellationToken);
    }

    private static Task<ServiceRecordView> RejectAsync(
        Guid serviceRecordId, ServiceRecordRejectRequest request, ClaimsPrincipal user,
        IAdminServiceRecordsService service, CancellationToken cancellationToken)
        => service.RejectAsync(user.GetRequiredUserId(), serviceRecordId, request, cancellationToken);
}
