using Barbershop.Application.Auth;
using Barbershop.Application.Staff.Admin;

namespace Api.Barbershop.Features.Admin.Staff;

/// <summary>The general services a professional offers and their duration (ADR-0016 B2), managed by administration.</summary>
public static class AdminStaffOfferingsEndpoints
{
  public static RouteGroupBuilder MapAdminStaffOfferingsEndpoints(this RouteGroupBuilder api)
  {
    var offerings = api.MapGroup("/admin/staff/{staffProfileId:guid}/offerings")
        .WithTags("Admin")
        .RequireAuthorization(AuthPolicyNames.Admin);

    offerings.MapGet(string.Empty, GetAsync)
        .WithName("GetAdminStaffOfferings")
        .Produces<StaffServiceOfferingsView>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    offerings.MapPut(string.Empty, ReplaceAsync)
        .WithName("ReplaceAdminStaffOfferings")
        .Produces<StaffServiceOfferingsView>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

    return api;
  }

  private static Task<StaffServiceOfferingsView> GetAsync(
      Guid staffProfileId, IAdminStaffOfferingsService service, CancellationToken cancellationToken)
      => service.GetAsync(staffProfileId, cancellationToken);

  private static Task<StaffServiceOfferingsView> ReplaceAsync(
      Guid staffProfileId,
      StaffServiceOfferingsReplaceRequest request,
      IAdminStaffOfferingsService service,
      CancellationToken cancellationToken)
      => service.ReplaceAsync(staffProfileId, request, cancellationToken);
}
