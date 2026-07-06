using System.Security.Claims;
using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Barbershop.Application.Finance.Admin;

namespace Api.Barbershop.Features.Admin.Finance;

public static class AdminIncomeEndpoints
{
    public static RouteGroupBuilder MapAdminIncomeEndpoints(this RouteGroupBuilder api)
    {
        var adminIncome = api.MapGroup("/admin/income")
            .WithTags("Admin")
            .RequireAuthorization(AuthPolicyNames.Admin);

        adminIncome.MapGet(string.Empty, GetAsync)
            .WithName("GetAdminIncome")
            .Produces<IReadOnlyList<IncomeEntryView>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminIncome.MapPost(string.Empty, CreateAsync)
            .WithName("CreateAdminIncome")
            .Produces<IncomeEntryView>(StatusCodes.Status201Created)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminIncome.MapPut("/{incomeEntryId:guid}", UpdateAsync)
            .WithName("UpdateAdminIncome")
            .Produces<IncomeEntryView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        adminIncome.MapDelete("/{incomeEntryId:guid}", SoftDeleteAsync)
            .WithName("DeleteAdminIncome")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static Task<IReadOnlyList<IncomeEntryView>> GetAsync(
        DateOnly? date,
        Guid? staffProfileId,
        IAdminIncomeService service,
        CancellationToken cancellationToken)
        => service.GetAsync(date, staffProfileId, cancellationToken);

    private static async Task<IResult> CreateAsync(
        IncomeEntryCreateRequest request,
        ClaimsPrincipal user,
        IAdminIncomeService service,
        CancellationToken cancellationToken)
    {
        var response = await service.CreateAsync(user.GetRequiredUserId(), request, cancellationToken);
        return Results.Created($"/api/v1/admin/income/{response.Id}", response);
    }

    private static Task<IncomeEntryView> UpdateAsync(
        Guid incomeEntryId,
        IncomeEntryUpdateRequest request,
        IAdminIncomeService service,
        CancellationToken cancellationToken)
        => service.UpdateAsync(incomeEntryId, request, cancellationToken);

    private static async Task<IResult> SoftDeleteAsync(
        Guid incomeEntryId,
        IAdminIncomeService service,
        CancellationToken cancellationToken)
    {
        await service.SoftDeleteAsync(incomeEntryId, cancellationToken);
        return Results.NoContent();
    }
}
