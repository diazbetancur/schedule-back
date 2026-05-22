using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Barbershop.Application.Availability;
using System.Security.Claims;

namespace Api.Barbershop.Features.Staff.Availability;

public static class StaffAvailabilityEndpoints
{
  public static RouteGroupBuilder MapStaffAvailabilityEndpoints(this RouteGroupBuilder api)
  {
    var availability = api.MapGroup("/staff/availability")
        .WithTags("Staff")
        .RequireAuthorization(AuthPolicyNames.Staff);

    availability.MapGet(string.Empty, GetCurrentAsync)
        .WithName("GetCurrentStaffAvailability")
        .Produces<AvailabilitySummaryResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    availability.MapPut("/rules", ReplaceRulesAsync)
        .WithName("ReplaceCurrentStaffAvailabilityRules")
        .Produces<IReadOnlyList<AvailabilityRuleResponse>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    availability.MapGet("/unavailable-periods", GetUnavailablePeriodsAsync)
        .WithName("GetCurrentStaffUnavailablePeriods")
        .Produces<IReadOnlyList<UnavailablePeriodResponse>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    availability.MapPost("/unavailable-periods", CreateUnavailablePeriodAsync)
        .WithName("CreateCurrentStaffUnavailablePeriod")
        .Produces<UnavailablePeriodResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    availability.MapPut("/unavailable-periods/{unavailablePeriodId:guid}", UpdateUnavailablePeriodAsync)
        .WithName("UpdateCurrentStaffUnavailablePeriod")
        .Produces<UnavailablePeriodResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    availability.MapDelete("/unavailable-periods/{unavailablePeriodId:guid}", DeleteUnavailablePeriodAsync)
        .WithName("DeleteCurrentStaffUnavailablePeriod")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    return api;
  }

  private static Task<AvailabilitySummaryResponse> GetCurrentAsync(ClaimsPrincipal user, IStaffAvailabilityService service, CancellationToken cancellationToken)
      => service.GetCurrentAsync(user.GetRequiredUserId(), cancellationToken);

  private static Task<IReadOnlyList<AvailabilityRuleResponse>> ReplaceRulesAsync(
      ClaimsPrincipal user,
      AvailabilityRuleRequest[] rules,
      IStaffAvailabilityService service,
      CancellationToken cancellationToken)
      => service.ReplaceRulesAsync(user.GetRequiredUserId(), rules, cancellationToken);

  private static Task<IReadOnlyList<UnavailablePeriodResponse>> GetUnavailablePeriodsAsync(
      ClaimsPrincipal user,
      IStaffAvailabilityService service,
      CancellationToken cancellationToken)
      => service.GetUnavailablePeriodsAsync(user.GetRequiredUserId(), cancellationToken);

  private static async Task<IResult> CreateUnavailablePeriodAsync(
      ClaimsPrincipal user,
      UnavailablePeriodCreateRequest request,
      IStaffAvailabilityService service,
      CancellationToken cancellationToken)
  {
    var response = await service.CreateUnavailablePeriodAsync(user.GetRequiredUserId(), request, cancellationToken);
    return Results.Created($"/api/v1/staff/availability/unavailable-periods/{response.Id}", response);
  }

  private static Task<UnavailablePeriodResponse> UpdateUnavailablePeriodAsync(
      ClaimsPrincipal user,
      Guid unavailablePeriodId,
      UnavailablePeriodUpdateRequest request,
      IStaffAvailabilityService service,
      CancellationToken cancellationToken)
      => service.UpdateUnavailablePeriodAsync(user.GetRequiredUserId(), unavailablePeriodId, request, cancellationToken);

  private static async Task<IResult> DeleteUnavailablePeriodAsync(
      ClaimsPrincipal user,
      Guid unavailablePeriodId,
      IStaffAvailabilityService service,
      CancellationToken cancellationToken)
  {
    await service.DeleteUnavailablePeriodAsync(user.GetRequiredUserId(), unavailablePeriodId, cancellationToken);
    return Results.NoContent();
  }
}