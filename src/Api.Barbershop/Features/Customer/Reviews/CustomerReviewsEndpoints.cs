using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Barbershop.Application.Reviews;
using System.Security.Claims;

namespace Api.Barbershop.Features.Customer.Reviews;

public static class CustomerReviewsEndpoints
{
  public static RouteGroupBuilder MapCustomerReviewsEndpoints(this RouteGroupBuilder api)
  {
    var customer = api.MapGroup("/customer")
        .WithTags("Customer")
        .RequireAuthorization(AuthPolicyNames.Customer);

    customer.MapGet("/reviews", GetCurrentCustomerReviewsAsync)
        .WithName("GetCustomerReviews")
        .Produces<IReadOnlyList<CustomerReviewView>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

    customer.MapPost("/appointments/{appointmentId:guid}/review", CreateReviewAsync)
        .WithName("CreateCustomerAppointmentReview")
        .Produces<CustomerReviewView>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    return api;
  }

  private static Task<IReadOnlyList<CustomerReviewView>> GetCurrentCustomerReviewsAsync(
      ClaimsPrincipal user,
      ICustomerReviewsService service,
      CancellationToken cancellationToken)
      => service.GetByCurrentCustomerAsync(user.GetRequiredUserId(), cancellationToken);

  private static async Task<IResult> CreateReviewAsync(
      ClaimsPrincipal user,
      Guid appointmentId,
      ReviewCreateRequest request,
      ICustomerReviewsService service,
      CancellationToken cancellationToken)
  {
    var response = await service.CreateAsync(user.GetRequiredUserId(), appointmentId, request, cancellationToken);
    return Results.Created($"/api/v1/customer/appointments/{appointmentId}/review", response);
  }
}
