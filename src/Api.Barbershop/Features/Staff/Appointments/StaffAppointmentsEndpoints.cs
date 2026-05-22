using Api.Barbershop.Features.Auth;
using Barbershop.Application.Appointments;
using Barbershop.Application.Auth;
using System.Security.Claims;

namespace Api.Barbershop.Features.Staff.Appointments;

public static class StaffAppointmentsEndpoints
{
  public static RouteGroupBuilder MapStaffAppointmentsEndpoints(this RouteGroupBuilder api)
  {
    var appointments = api.MapGroup("/staff/appointments")
        .WithTags("Staff")
        .RequireAuthorization(AuthPolicyNames.Staff);

    appointments.MapGet(string.Empty, GetForCurrentStaffAsync)
        .WithName("GetCurrentStaffAppointments")
        .Produces<IReadOnlyList<AppointmentView>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

    appointments.MapPost(string.Empty, CreateForCurrentStaffAsync)
        .WithName("CreateCurrentStaffAppointment")
        .Produces<AppointmentView>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    appointments.MapPut("/{appointmentId:guid}", UpdateForCurrentStaffAsync)
        .WithName("UpdateCurrentStaffAppointment")
        .Produces<AppointmentView>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    appointments.MapPatch("/{appointmentId:guid}/status", UpdateStatusForCurrentStaffAsync)
        .WithName("UpdateCurrentStaffAppointmentStatus")
        .Produces<AppointmentView>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    return api;
  }

  private static Task<IReadOnlyList<AppointmentView>> GetForCurrentStaffAsync(
      ClaimsPrincipal user,
      IStaffAppointmentsService service,
      CancellationToken cancellationToken)
      => service.GetForCurrentStaffAsync(user.GetRequiredUserId(), cancellationToken);

  private static async Task<IResult> CreateForCurrentStaffAsync(
      ClaimsPrincipal user,
      StaffManualAppointmentCreateRequest request,
      IStaffAppointmentsService service,
      CancellationToken cancellationToken)
  {
    var response = await service.CreateForCurrentStaffAsync(user.GetRequiredUserId(), request, cancellationToken);
    return Results.Created($"/api/v1/staff/appointments/{response.Id}", response);
  }

  private static Task<AppointmentView> UpdateForCurrentStaffAsync(
      ClaimsPrincipal user,
      Guid appointmentId,
      AppointmentUpdateRequest request,
      IStaffAppointmentsService service,
      CancellationToken cancellationToken)
      => service.UpdateForCurrentStaffAsync(user.GetRequiredUserId(), appointmentId, request, cancellationToken);

  private static Task<AppointmentView> UpdateStatusForCurrentStaffAsync(
      ClaimsPrincipal user,
      Guid appointmentId,
      AppointmentStatusUpdateRequest request,
      IStaffAppointmentsService service,
      CancellationToken cancellationToken)
      => service.UpdateStatusForCurrentStaffAsync(user.GetRequiredUserId(), appointmentId, request, cancellationToken);
}
