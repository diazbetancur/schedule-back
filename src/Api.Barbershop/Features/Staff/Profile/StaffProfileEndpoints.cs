using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Staff;
using Barbershop.Application.Staff.SelfService;
using System.Security.Claims;

namespace Api.Barbershop.Features.Staff.Profile;

public static class StaffProfileEndpoints
{
    private const long MaxPhotoBytes = 1_048_576; // 1 MB
    private const long MaxQrBytes = 1_048_576;    // 1 MB

    public static RouteGroupBuilder MapStaffProfileEndpoints(this RouteGroupBuilder api)
    {
        var staffProfile = api.MapGroup("/staff/profile")
            .WithTags("Staff")
            .RequireAuthorization(AuthPolicyNames.Staff);

        staffProfile.MapGet(string.Empty, GetCurrentAsync)
            .WithName("GetCurrentStaffProfile")
            .Produces<StaffManagementView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        staffProfile.MapPut(string.Empty, UpdateCurrentAsync)
            .WithName("UpdateCurrentStaffProfile")
            .Produces<StaffManagementView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        staffProfile.MapPost("/photo", UploadPhotoAsync)
            .WithName("UploadStaffProfilePhoto")
            .Produces<StaffManagementView>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .DisableAntiforgery();

        staffProfile.MapDelete("/photo", RemovePhotoAsync)
            .WithName("RemoveStaffProfilePhoto")
            .Produces<StaffManagementView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        staffProfile.MapPost("/tips-qr", UploadTipsQrAsync)
            .WithName("UploadStaffTipsQr")
            .Produces<StaffManagementView>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .DisableAntiforgery();

        staffProfile.MapDelete("/tips-qr", RemoveTipsQrAsync)
            .WithName("RemoveStaffTipsQr")
            .Produces<StaffManagementView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return api;
    }

    private static Task<StaffManagementView> GetCurrentAsync(ClaimsPrincipal user, IStaffProfileService service, CancellationToken cancellationToken)
        => service.GetCurrentAsync(user.GetRequiredUserId(), cancellationToken);

    private static Task<StaffManagementView> UpdateCurrentAsync(ClaimsPrincipal user, StaffProfileUpdateRequest request, IStaffProfileService service, CancellationToken cancellationToken)
        => service.UpdateCurrentAsync(user.GetRequiredUserId(), request, cancellationToken);

    private static async Task<IResult> UploadPhotoAsync(ClaimsPrincipal user, HttpRequest request, IStaffProfileService service, CancellationToken cancellationToken)
    {
        var file = await ReadSingleFileAsync(request, "photo", MaxPhotoBytes, cancellationToken);
        using var stream = file.OpenReadStream();
        var result = await service.UploadPhotoAsync(
            user.GetRequiredUserId(),
            new StaffMediaUploadRequest(file.FileName, file.ContentType, file.Length, stream),
            cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> RemovePhotoAsync(ClaimsPrincipal user, IStaffProfileService service, CancellationToken cancellationToken)
    {
        var result = await service.RemovePhotoAsync(user.GetRequiredUserId(), cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> UploadTipsQrAsync(ClaimsPrincipal user, HttpRequest request, IStaffProfileService service, CancellationToken cancellationToken)
    {
        var file = await ReadSingleFileAsync(request, "file", MaxQrBytes, cancellationToken);
        using var stream = file.OpenReadStream();
        var result = await service.UploadTipsQrAsync(
            user.GetRequiredUserId(),
            new StaffMediaUploadRequest(file.FileName, file.ContentType, file.Length, stream),
            cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> RemoveTipsQrAsync(ClaimsPrincipal user, IStaffProfileService service, CancellationToken cancellationToken)
    {
        var result = await service.RemoveTipsQrAsync(user.GetRequiredUserId(), cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IFormFile> ReadSingleFileAsync(HttpRequest request, string fieldName, long maxBytes, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>
            {
                [fieldName] = ["Multipart form-data content is required."]
            });
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile(fieldName);

        if (file is null || file.Length == 0)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>
            {
                [fieldName] = ["A file is required."]
            });
        }

        if (file.Length > maxBytes)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>
            {
                [fieldName] = [$"File size must not exceed {maxBytes / 1024 / 1024} MB."]
            });
        }

        return file;
    }
}
