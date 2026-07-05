using System.Security.Claims;
using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Barbershop.Application.Finance.Admin;

namespace Api.Barbershop.Features.Admin.Finance;

public static class AdminExpensesEndpoints
{
    public static RouteGroupBuilder MapAdminExpensesEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/admin/expenses")
            .WithTags("Admin")
            .RequireAuthorization(AuthPolicyNames.Admin);

        group.MapGet(string.Empty, GetAsync)
            .WithName("GetAdminExpenses")
            .Produces<IReadOnlyList<ExpenseEntryView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost(string.Empty, CreateAsync)
            .WithName("CreateAdminExpense")
            .Produces<ExpenseEntryView>(StatusCodes.Status201Created)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/{expenseEntryId:guid}", UpdateAsync)
            .WithName("UpdateAdminExpense")
            .Produces<ExpenseEntryView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapDelete("/{expenseEntryId:guid}", SoftDeleteAsync)
            .WithName("DeleteAdminExpense")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static Task<IReadOnlyList<ExpenseEntryView>> GetAsync(
        int? year,
        int? month,
        IAdminExpensesService service,
        CancellationToken cancellationToken)
        => service.GetAsync(year, month, cancellationToken);

    private static async Task<IResult> CreateAsync(
        ExpenseEntryCreateRequest request,
        ClaimsPrincipal user,
        IAdminExpensesService service,
        CancellationToken cancellationToken)
    {
        var response = await service.CreateAsync(user.GetRequiredUserId(), request, cancellationToken);
        return Results.Created($"/api/v1/admin/expenses/{response.Id}", response);
    }

    private static Task<ExpenseEntryView> UpdateAsync(
        Guid expenseEntryId,
        ExpenseEntryUpdateRequest request,
        IAdminExpensesService service,
        CancellationToken cancellationToken)
        => service.UpdateAsync(expenseEntryId, request, cancellationToken);

    private static async Task<IResult> SoftDeleteAsync(
        Guid expenseEntryId,
        IAdminExpensesService service,
        CancellationToken cancellationToken)
    {
        await service.SoftDeleteAsync(expenseEntryId, cancellationToken);
        return Results.NoContent();
    }
}
