using System.Security.Claims;
using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Barbershop.Application.Customers.Admin;

namespace Api.Barbershop.Features.Admin.Customers;

public static class AdminCustomersEndpoints
{
    public static RouteGroupBuilder MapAdminCustomersEndpoints(this RouteGroupBuilder api)
    {
        var adminCustomers = api.MapGroup("/admin/customers")
            .WithTags("Admin")
            .RequireAuthorization(AuthPolicyNames.Admin);

        adminCustomers.MapGet(string.Empty, SearchAsync)
            .WithName("GetAdminCustomers")
            .Produces<IReadOnlyList<CustomerListItem>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminCustomers.MapGet("/duplicates", FindDuplicatesAsync)
            .WithName("GetAdminCustomerDuplicates")
            .Produces<IReadOnlyList<CustomerDuplicateCandidate>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminCustomers.MapGet("/account-candidates", GetAccountCandidatesAsync)
            .WithName("GetAdminCustomerAccountCandidates")
            .Produces<IReadOnlyList<CustomerAccountCandidate>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminCustomers.MapGet("/{customerId:guid}", GetByIdAsync)
            .WithName("GetAdminCustomerById")
            .Produces<CustomerDetailView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminCustomers.MapPost(string.Empty, CreateAsync)
            .WithName("CreateAdminCustomer")
            .Produces<CustomerDetailView>(StatusCodes.Status201Created)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminCustomers.MapPost("/from-account", CreateFromAccountAsync)
            .WithName("CreateAdminCustomerFromAccount")
            .Produces<CustomerDetailView>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminCustomers.MapPut("/{customerId:guid}", UpdateAsync)
            .WithName("UpdateAdminCustomer")
            .Produces<CustomerDetailView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminCustomers.MapPost("/{customerId:guid}/link-account", LinkAccountAsync)
            .WithName("LinkAdminCustomerAccount")
            .Produces<CustomerDetailView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminCustomers.MapPost("/{customerId:guid}/unlink-account", UnlinkAccountAsync)
            .WithName("UnlinkAdminCustomerAccount")
            .Produces<CustomerDetailView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminCustomers.MapGet("/{customerId:guid}/merge-preview", GetMergePreviewAsync)
            .WithName("GetAdminCustomerMergePreview")
            .Produces<CustomerMergePreview>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminCustomers.MapPost("/{customerId:guid}/merge", MergeAsync)
            .WithName("MergeAdminCustomer")
            .Produces<CustomerDetailView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static Task<IReadOnlyList<CustomerListItem>> SearchAsync(
        string? q, IAdminCustomersService service, CancellationToken cancellationToken)
        => service.SearchAsync(q, cancellationToken);

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
