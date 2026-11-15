using System.Security.Claims;
using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Barbershop.Application.Common.Paging;
using Barbershop.Application.Customers.Admin;

namespace Api.Barbershop.Features.Admin.Customers;

public static class AdminCustomersEndpoints
{
    public static RouteGroupBuilder MapAdminCustomersEndpoints(this RouteGroupBuilder api)
    {
        var adminCustomers = api.MapGroup("/admin/customers")
            .WithTags("Admin");

        adminCustomers.MapGet(string.Empty, SearchAsync)
            .RequireAuthorization(AuthPolicyNames.CustomerLoyaltyOrPaymentsAccess)
            .WithName("GetAdminCustomers")
            .Produces<PagedResult<CustomerListItem>>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminCustomers.MapGet("/duplicates", FindDuplicatesAsync)
            .RequireAuthorization(AuthPolicyNames.Admin)
            .WithName("GetAdminCustomerDuplicates")
            .Produces<IReadOnlyList<CustomerDuplicateCandidate>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminCustomers.MapGet("/account-candidates", GetAccountCandidatesAsync)
            .RequireAuthorization(AuthPolicyNames.Admin)
            .WithName("GetAdminCustomerAccountCandidates")
            .Produces<IReadOnlyList<CustomerAccountCandidate>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminCustomers.MapGet("/{customerId:guid}", GetByIdAsync)
            .RequireAuthorization(AuthPolicyNames.CustomerLoyaltyOrPaymentsAccess)
            .WithName("GetAdminCustomerById")
            .Produces<CustomerDetailView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminCustomers.MapPost(string.Empty, CreateAsync)
            .RequireAuthorization(AuthPolicyNames.Admin)
            .WithName("CreateAdminCustomer")
            .Produces<CustomerDetailView>(StatusCodes.Status201Created)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminCustomers.MapPost("/from-account", CreateFromAccountAsync)
            .RequireAuthorization(AuthPolicyNames.Admin)
            .WithName("CreateAdminCustomerFromAccount")
            .Produces<CustomerDetailView>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminCustomers.MapPut("/{customerId:guid}", UpdateAsync)
            .RequireAuthorization(AuthPolicyNames.Admin)
            .WithName("UpdateAdminCustomer")
            .Produces<CustomerDetailView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminCustomers.MapPost("/{customerId:guid}/link-account", LinkAccountAsync)
            .RequireAuthorization(AuthPolicyNames.Admin)
            .WithName("LinkAdminCustomerAccount")
            .Produces<CustomerDetailView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminCustomers.MapPost("/{customerId:guid}/unlink-account", UnlinkAccountAsync)
            .RequireAuthorization(AuthPolicyNames.Admin)
            .WithName("UnlinkAdminCustomerAccount")
            .Produces<CustomerDetailView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminCustomers.MapGet("/{customerId:guid}/merge-preview", GetMergePreviewAsync)
            .RequireAuthorization(AuthPolicyNames.Admin)
            .WithName("GetAdminCustomerMergePreview")
            .Produces<CustomerMergePreview>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminCustomers.MapPost("/{customerId:guid}/merge", MergeAsync)
            .RequireAuthorization(AuthPolicyNames.Admin)
            .WithName("MergeAdminCustomer")
            .Produces<CustomerDetailView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static Task<PagedResult<CustomerListItem>> SearchAsync(
        string? q, int? page, int? pageSize, IAdminCustomersService service, CancellationToken cancellationToken)
        => service.SearchAsync(q, page, pageSize, cancellationToken);

    private static Task<IReadOnlyList<CustomerDuplicateCandidate>> FindDuplicatesAsync(
        string? email,
        string? phone,
        Guid? excludeCustomerId,
        Guid? excludeUserId,
        IAdminCustomersService service,
        CancellationToken cancellationToken)
        => service.FindDuplicatesAsync(email, phone, excludeCustomerId, excludeUserId, cancellationToken);

    private static async Task<IResult> CreateFromAccountAsync(
        CustomerCreateFromAccountRequest request, ClaimsPrincipal user, IAdminCustomersService service, CancellationToken cancellationToken)
    {
        var response = await service.CreateFromAccountAsync(user.GetRequiredUserId(), request, cancellationToken);
        return Results.Created($"/api/v1/admin/customers/{response.Id}", response);
    }

    private static Task<IReadOnlyList<CustomerAccountCandidate>> GetAccountCandidatesAsync(
        string? q, IAdminCustomersService service, CancellationToken cancellationToken)
        => service.GetAccountCandidatesAsync(q, cancellationToken);

    private static Task<CustomerDetailView> GetByIdAsync(
        Guid customerId, IAdminCustomersService service, CancellationToken cancellationToken)
        => service.GetByIdAsync(customerId, cancellationToken);

    private static async Task<IResult> CreateAsync(
        CustomerCreateRequest request, ClaimsPrincipal user, IAdminCustomersService service, CancellationToken cancellationToken)
    {
        var response = await service.CreateAsync(user.GetRequiredUserId(), request, cancellationToken);
        return Results.Created($"/api/v1/admin/customers/{response.Id}", response);
    }

    private static Task<CustomerDetailView> UpdateAsync(
        Guid customerId, CustomerUpdateRequest request, IAdminCustomersService service, CancellationToken cancellationToken)
        => service.UpdateAsync(customerId, request, cancellationToken);

    private static Task<CustomerDetailView> LinkAccountAsync(
        Guid customerId, CustomerLinkAccountRequest request, ClaimsPrincipal user, IAdminCustomersService service, CancellationToken cancellationToken)
        => service.LinkAccountAsync(user.GetRequiredUserId(), customerId, request, cancellationToken);

    private static Task<CustomerDetailView> UnlinkAccountAsync(
        Guid customerId, CustomerUnlinkAccountRequest request, ClaimsPrincipal user, IAdminCustomersService service, CancellationToken cancellationToken)
        => service.UnlinkAccountAsync(user.GetRequiredUserId(), customerId, request, cancellationToken);

    private static Task<CustomerMergePreview> GetMergePreviewAsync(
        Guid customerId, Guid sourceCustomerId, IAdminCustomersService service, CancellationToken cancellationToken)
        => service.GetMergePreviewAsync(customerId, sourceCustomerId, cancellationToken);

    private static Task<CustomerDetailView> MergeAsync(
        Guid customerId, CustomerMergeRequest request, ClaimsPrincipal user, IAdminCustomersService service, CancellationToken cancellationToken)
        => service.MergeAsync(user.GetRequiredUserId(), customerId, request, cancellationToken);
}
