using Barbershop.Application.Auth;
using Barbershop.Application.Availability;

namespace Api.Barbershop.Features.Admin.Staff;

public static class AdminStaffAvailabilityEndpoints
{
  public static RouteGroupBuilder MapAdminStaffAvailabilityEndpoints(this RouteGroupBuilder api)
  {
    var availability = api.MapGroup("/admin/staff/{staffProfileId:guid}/availability")
        .WithTags("Admin")
        .RequireAuthorization(AuthPolicyNames.Admin);

    availability.MapGet(string.Empty, GetAsync)
        .WithName("GetAdminStaffAvailability")
        .Produces<AvailabilitySummaryResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    availability.MapPut("/rules", ReplaceRulesAsync)
        .WithName("ReplaceAdminStaffAvailabilityRules")
        .Produces<IReadOnlyList<AvailabilityRuleResponse>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    availability.MapGet("/unavailable-periods", GetUnavailablePeriodsAsync)
        .WithName("GetAdminStaffUnavailablePeriods")
        .Produces<IReadOnlyList<UnavailablePeriodResponse>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    availability.MapPost("/unavailable-periods", CreateUnavailablePeriodAsync)
        .WithName("CreateAdminStaffUnavailablePeriod")
        .Produces<UnavailablePeriodResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    availability.MapPut("/unavailable-periods/{unavailablePeriodId:guid}", UpdateUnavailablePeriodAsync)
        .WithName("UpdateAdminStaffUnavailablePeriod")
        .Produces<UnavailablePeriodResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    availability.MapDelete("/unavailable-periods/{unavailablePeriodId:guid}", DeleteUnavailablePeriodAsync)
        .WithName("DeleteAdminStaffUnavailablePeriod")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    return api;
  }

  private static Task<AvailabilitySummaryResponse> GetAsync(Guid staffProfileId, IAdminStaffAvailabilityService service, CancellationToken cancellationToken)
      => service.GetAsync(staffProfileId, cancellationToken);

  private static Task<IReadOnlyList<AvailabilityRuleResponse>> ReplaceRulesAsync(
      Guid staffProfileId,
      AvailabilityRuleRequest[] rules,
      IAdminStaffAvailabilityService service,
      CancellationToken cancellationToken)
      => service.ReplaceRulesAsync(staffProfileId, rules, cancellationToken);

  private static Task<IReadOnlyList<UnavailablePeriodResponse>> GetUnavailablePeriodsAsync(
      Guid staffProfileId,
      IAdminStaffAvailabilityService service,
      CancellationToken cancellationToken)
      => service.GetUnavailablePeriodsAsync(staffProfileId, cancellationToken);

  private static async Task<IResult> CreateUnavailablePeriodAsync(
      Guid staffProfileId,
      UnavailablePeriodCreateRequest request,
      IAdminStaffAvailabilityService service,
      CancellationToken cancellationToken)
  {
    var response = await service.CreateUnavailablePeriodAsync(staffProfileId, request, cancellationToken);
    return Results.Created($"/api/v1/admin/staff/{staffProfileId}/availability/unavailable-periods/{response.Id}", response);
  }

  private static Task<UnavailablePeriodResponse> UpdateUnavailablePeriodAsync(
      Guid staffProfileId,
      Guid unavailablePeriodId,
      UnavailablePeriodUpdateRequest request,
      IAdminStaffAvailabilityService service,
      CancellationToken cancellationToken)
      => service.UpdateUnavailablePeriodAsync(staffProfileId, unavailablePeriodId, request, cancellationToken);

  private static async Task<IResult> DeleteUnavailablePeriodAsync(
      Guid staffProfileId,
      Guid unavailablePeriodId,
      IAdminStaffAvailabilityService service,
      CancellationToken cancellationToken)
  {
    await service.DeleteUnavailablePeriodAsync(staffProfileId, unavailablePeriodId, cancellationToken);
    return Results.NoContent();
  }
}