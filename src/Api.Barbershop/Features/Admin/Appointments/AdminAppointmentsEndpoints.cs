using Barbershop.Application.Appointments;
using Barbershop.Application.Auth;
using Barbershop.Application.Common.Paging;

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
        .Produces<PagedResult<AppointmentView>>(StatusCodes.Status200OK)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

    appointments.MapGet("/service-options", GetServiceOptionsAsync)
        .WithName("GetAdminAppointmentServiceOptions")
        .Produces<IReadOnlyList<AppointmentServiceOption>>(StatusCodes.Status200OK)
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

  private static Task<PagedResult<AppointmentView>> GetAsync(
      [AsParameters] AdminAppointmentListQuery query,
      IAdminAppointmentsService service,
      CancellationToken cancellationToken)
      => service.GetAsync(query, cancellationToken);

  private static Task<IReadOnlyList<AppointmentServiceOption>> GetServiceOptionsAsync(
      Guid staffProfileId,
      IAdminAppointmentsService service,
      CancellationToken cancellationToken)
      => service.GetServiceOptionsAsync(staffProfileId, cancellationToken);

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
