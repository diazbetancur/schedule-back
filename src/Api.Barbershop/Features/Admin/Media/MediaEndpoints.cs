using System.Security.Claims;
using Api.Barbershop.Features.Auth;
using Barbershop.Application.Auth;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Media;
using Api.Barbershop.Features.Uploads;
using Barbershop.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Api.Barbershop.Features.Admin.Media;

public static class MediaEndpoints
{
  public static RouteGroupBuilder MapMediaEndpoints(this RouteGroupBuilder api)
  {
    var media = api.MapGroup("/media")
        .WithTags("Media")
        .RequireAuthorization();

    media.MapPost("/upload", UploadAsync)
        .WithName("UploadMediaAsset")
        .Produces<MediaAssetView>(StatusCodes.Status201Created)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .RequireAuthorization(policy => policy.RequireRole(AuthRoleNames.Admin, AuthRoleNames.Staff, AuthRoleNames.Customer));

    media.MapGet(string.Empty, GetAllAsync)
        .WithName("GetMediaAssets")
        .Produces<IReadOnlyList<MediaAssetView>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequireAuthorization(AuthPolicyNames.Admin);

    media.MapGet("/{mediaAssetId:guid}", GetByIdAsync)
        .WithName("GetMediaAssetById")
        .Produces<MediaAssetView>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAuthorization(AuthPolicyNames.Admin);

    media.MapDelete("/{mediaAssetId:guid}", DeleteAsync)
        .WithName("DeleteMediaAsset")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .RequireAuthorization(AuthPolicyNames.Admin);

    return api;
  }

  private static async Task<IResult> UploadAsync(
      ClaimsPrincipal user,
      HttpRequest request,
      IMediaAssetsService service,
      IOptions<FileStorageOptions> fileStorageOptions,
      CancellationToken cancellationToken)
  {
    if (!request.HasFormContentType)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        ["file"] = ["Envía el archivo como formulario (multipart/form-data)."]
      });
    }

    var form = await UploadRequestBody.ReadFormAsync(request, fileStorageOptions.Value.MaxUploadBytes, cancellationToken);
    var file = form.Files.GetFile("file");
    var purposeRaw = form["purpose"].ToString();

    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
    if (file is null)
    {
      errors["file"] = ["Selecciona un archivo."];
    }

    if (!MediaAssetPurposeParser.TryParse(purposeRaw, out var purpose))
    {
      errors["purpose"] = [MediaAssetPurposeParser.InvalidPurposeMessage];
    }

    if (errors.Count > 0)
    {
      throw new ValidationProblemException(errors);
    }

    using var stream = file!.OpenReadStream();

    var roles = user.FindAll(ClaimTypes.Role)
        .Select(claim => claim.Value)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    var response = await service.UploadAsync(
        user.GetRequiredUserId(),
        roles,
        new MediaAssetUploadRequest(file.FileName, file.ContentType, file.Length, purpose, stream),
        cancellationToken);

    return Results.Created($"/api/v1/media/{response.Id}", response);
  }

  private static Task<IReadOnlyList<MediaAssetView>> GetAllAsync(IMediaAssetsService service, CancellationToken cancellationToken)
      => service.GetAllAsync(cancellationToken);

  private static Task<MediaAssetView> GetByIdAsync(Guid mediaAssetId, IMediaAssetsService service, CancellationToken cancellationToken)
      => service.GetByIdAsync(mediaAssetId, cancellationToken);

  private static async Task<IResult> DeleteAsync(Guid mediaAssetId, IMediaAssetsService service, CancellationToken cancellationToken)
  {
    await service.DeleteAsync(mediaAssetId, cancellationToken);
    return Results.NoContent();
  }
}
