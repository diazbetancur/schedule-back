using Api.Barbershop.Features.Auth;
using Barbershop.Application.Appointments;
using Barbershop.Application.Auth;
using System.Security.Claims;

namespace Api.Barbershop.Features.Customer.Appointments;

public static class CustomerAppointmentsEndpoints
{
  public static RouteGroupBuilder MapCustomerAppointmentsEndpoints(this RouteGroupBuilder api)
  {
    var appointments = api.MapGroup("/customer/appointments")
        .WithTags("Customer")
        .RequireAuthorization(AuthPolicyNames.Customer);

    appointments.MapGet(string.Empty, GetHistoryAsync)
        .WithName("GetCustomerAppointments")
        .Produces<IReadOnlyList<AppointmentView>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

    appointments.MapPost(string.Empty, CreateAsync)
        .WithName("CreateCustomerAppointment")
        .Produces<AppointmentView>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    appointments.MapPatch("/{appointmentId:guid}/cancel", CancelAsync)
        .WithName("CancelCustomerAppointment")
        .Produces<AppointmentView>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    return api;
  }

  private static Task<IReadOnlyList<AppointmentView>> GetHistoryAsync(
      ClaimsPrincipal user,
      ICustomerAppointmentsService service,
      CancellationToken cancellationToken)
      => service.GetHistoryAsync(user.GetRequiredUserId(), cancellationToken);

  private static async Task<IResult> CreateAsync(
      ClaimsPrincipal user,
      CustomerAppointmentCreateRequest request,
      ICustomerAppointmentsService service,
      CancellationToken cancellationToken)
  {
    var response = await service.CreateAsync(user.GetRequiredUserId(), request, cancellationToken);
    return Results.Created($"/api/v1/customer/appointments/{response.Id}", response);
  }

  private static Task<AppointmentView> CancelAsync(
      ClaimsPrincipal user,
      Guid appointmentId,
      ICustomerAppointmentsService service,
      CancellationToken cancellationToken)
      => service.CancelAsync(user.GetRequiredUserId(), appointmentId, cancellationToken);
}
