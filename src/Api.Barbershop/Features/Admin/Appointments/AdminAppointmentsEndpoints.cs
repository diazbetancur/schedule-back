using Barbershop.Application.Appointments;
using Barbershop.Application.Auth;

namespace Api.Barbershop.Features.Admin.Appointments;

public static class AdminAppointmentsEndpoints
{
  public static RouteGroupBuilder MapAdminAppointmentsEndpoints(this RouteGroupBuilder api)
  {
    var appointments = api.MapGroup("/admin/appointments")
        .WithTags("Admin")
        .RequireAuthorization(AuthPolicyNames.Admin);

    appointments.MapGet(string.Empty, GetAsync)
        .WithName("GetAdminAppointments")
        .Produces<IReadOnlyList<AppointmentView>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

    appointments.MapPost(string.Empty, CreateAsync)
        .WithName("CreateAdminAppointment")
        .Produces<AppointmentView>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    appointments.MapPut("/{appointmentId:guid}", UpdateAsync)
        .WithName("UpdateAdminAppointment")
        .Produces<AppointmentView>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    appointments.MapPatch("/{appointmentId:guid}/status", UpdateStatusAsync)
        .WithName("UpdateAdminAppointmentStatus")
        .Produces<AppointmentView>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    return api;
  }

  private static Task<IReadOnlyList<AppointmentView>> GetAsync(
      Guid? staffProfileId,
      IAdminAppointmentsService service,
      CancellationToken cancellationToken)
      => service.GetAsync(staffProfileId, cancellationToken);

  private static async Task<IResult> CreateAsync(
      AdminManualAppointmentCreateRequest request,
      IAdminAppointmentsService service,
      CancellationToken cancellationToken)
  {
    var response = await service.CreateAsync(request, cancellationToken);
    return Results.Created($"/api/v1/admin/appointments/{response.Id}", response);
  }

  private static Task<AppointmentView> UpdateAsync(
      Guid appointmentId,
      AppointmentUpdateRequest request,
      IAdminAppointmentsService service,
      CancellationToken cancellationToken)
      => service.UpdateAsync(appointmentId, request, cancellationToken);

  private static Task<AppointmentView> UpdateStatusAsync(
      Guid appointmentId,
      AppointmentStatusUpdateRequest request,
      IAdminAppointmentsService service,
      CancellationToken cancellationToken)
      => service.UpdateStatusAsync(appointmentId, request, cancellationToken);
}
