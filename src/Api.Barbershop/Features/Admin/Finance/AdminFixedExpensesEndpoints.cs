using Barbershop.Application.Auth;
using Barbershop.Application.Finance.Admin;

namespace Api.Barbershop.Features.Admin.Finance;

public static class AdminFixedExpensesEndpoints
{
    public static RouteGroupBuilder MapAdminFixedExpensesEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/admin/fixed-expenses")
            .WithTags("Admin")
            .RequireAuthorization(AuthPolicyNames.Admin);

        group.MapGet(string.Empty, GetAllAsync)
            .WithName("GetAdminFixedExpenses")
            .Produces<IReadOnlyList<FixedExpenseView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{fixedExpenseId:guid}", GetByIdAsync)
            .WithName("GetAdminFixedExpenseById")
            .Produces<FixedExpenseView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost(string.Empty, CreateAsync)
            .WithName("CreateAdminFixedExpense")
            .Produces<FixedExpenseView>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/{fixedExpenseId:guid}", UpdateAsync)
            .WithName("UpdateAdminFixedExpense")
            .Produces<FixedExpenseView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPatch("/{fixedExpenseId:guid}/status", UpdateStatusAsync)
            .WithName("UpdateAdminFixedExpenseStatus")
            .Produces<FixedExpenseView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapDelete("/{fixedExpenseId:guid}", SoftDeleteAsync)
            .WithName("DeleteAdminFixedExpense")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static Task<IReadOnlyList<FixedExpenseView>> GetAllAsync(IAdminFixedExpensesService service, CancellationToken cancellationToken)
        => service.GetAllAsync(cancellationToken);

    private static Task<FixedExpenseView> GetByIdAsync(Guid fixedExpenseId, IAdminFixedExpensesService service, CancellationToken cancellationToken)
        => service.GetByIdAsync(fixedExpenseId, cancellationToken);

    private static async Task<IResult> CreateAsync(FixedExpenseCreateRequest request, IAdminFixedExpensesService service, CancellationToken cancellationToken)
    {
        var response = await service.CreateAsync(request, cancellationToken);
        return Results.Created($"/api/v1/admin/fixed-expenses/{response.Id}", response);
    }

    private static Task<FixedExpenseView> UpdateAsync(Guid fixedExpenseId, FixedExpenseUpdateRequest request, IAdminFixedExpensesService service, CancellationToken cancellationToken)
        => service.UpdateAsync(fixedExpenseId, request, cancellationToken);

    private static Task<FixedExpenseView> UpdateStatusAsync(Guid fixedExpenseId, FixedExpenseStatusUpdateRequest request, IAdminFixedExpensesService service, CancellationToken cancellationToken)
        => service.UpdateStatusAsync(fixedExpenseId, request, cancellationToken);

    private static async Task<IResult> SoftDeleteAsync(Guid fixedExpenseId, IAdminFixedExpensesService service, CancellationToken cancellationToken)
    {
        await service.SoftDeleteAsync(fixedExpenseId, cancellationToken);
        return Results.NoContent();
    }
}
