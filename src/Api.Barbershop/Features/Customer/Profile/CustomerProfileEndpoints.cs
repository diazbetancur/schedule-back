using Api.Barbershop.Features.Auth;
using Api.Barbershop.Features.Uploads;
using Barbershop.Application.Auth;
using Barbershop.Application.Customer;
using System.Security.Claims;

namespace Api.Barbershop.Features.Customer.Profile;

public static class CustomerProfileEndpoints
{
    private const long MaxPhotoBytes = 10_485_760; // 10 MB

    public static RouteGroupBuilder MapCustomerProfileEndpoints(this RouteGroupBuilder api)
    {
        var profile = api.MapGroup("/customer/profile")
            .WithTags("Customer")
            .RequireAuthorization(AuthPolicyNames.Customer);

        profile.MapGet(string.Empty, GetAsync)
            .WithName("GetCustomerProfile")
            .Produces<CustomerProfileView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        profile.MapPatch(string.Empty, UpdateAsync)
            .WithName("UpdateCustomerProfile")
            .Produces<CustomerProfileView>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        profile.MapPost("/photo", UploadPhotoAsync)
            .WithName("UploadCustomerProfilePhoto")
            .Produces<CustomerProfileView>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .DisableAntiforgery();

        profile.MapDelete("/photo", RemovePhotoAsync)
            .WithName("RemoveCustomerProfilePhoto")
            .Produces<CustomerProfileView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        profile.MapPost("/password", ChangePasswordAsync)
            .WithName("ChangeCustomerPassword")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return api;
    }

    private static Task<CustomerProfileView> GetAsync(
        ClaimsPrincipal user,
        ICustomerProfileService service,
        CancellationToken cancellationToken)
        => service.GetAsync(user.GetRequiredUserId(), cancellationToken);

    private static Task<CustomerProfileView> UpdateAsync(
        ClaimsPrincipal user,
        CustomerProfileUpdateRequest request,
        ICustomerProfileService service,
        CancellationToken cancellationToken)
        => service.UpdateAsync(user.GetRequiredUserId(), request, cancellationToken);

    private static async Task<IResult> UploadPhotoAsync(
        ClaimsPrincipal user,
        HttpRequest request,
        ICustomerProfileService service,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        using var uploadedFile = await MultipartFileReader.ReadSingleFileAsync(
            request,
            "photo",
            "profile-photo",
            MaxPhotoBytes,
            loggerFactory.CreateLogger("Api.Barbershop.Uploads.CustomerProfilePhoto"),
            cancellationToken);
        var result = await service.UploadPhotoAsync(
            user.GetRequiredUserId(),
            new CustomerPhotoUploadRequest(
                uploadedFile.FileName,
                uploadedFile.ContentType,
                uploadedFile.Length,
                uploadedFile.Content),
            cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> RemovePhotoAsync(
        ClaimsPrincipal user,
        ICustomerProfileService service,
        CancellationToken cancellationToken)
    {
        var result = await service.RemovePhotoAsync(user.GetRequiredUserId(), cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> ChangePasswordAsync(
        ClaimsPrincipal user,
        CustomerPasswordChangeRequest request,
        ICustomerProfileService service,
        CancellationToken cancellationToken)
    {
        await service.ChangePasswordAsync(user.GetRequiredUserId(), request, cancellationToken);
        return Results.NoContent();
    }
}
