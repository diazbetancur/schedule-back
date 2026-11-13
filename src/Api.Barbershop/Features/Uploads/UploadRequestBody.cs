using Barbershop.Application.Common.Exceptions;
using Microsoft.AspNetCore.Http.Features;

namespace Api.Barbershop.Features.Uploads;

/// <summary>
/// Reads upload bodies safely (BUG-095). A body declared larger than the file limit is answered with 413 before
/// reading it, an undeclared (chunked) body is cut by the server at the same limit, and a multipart body that cannot
/// be read (malformed, without boundary, truncated) is answered with 400 instead of an unhandled 500.
/// </summary>
internal static class UploadRequestBody
{
  /// <summary>Room for the multipart boundaries, part headers and the small text fields sent with the file.</summary>
  internal const long MultipartOverheadBytes = 1024 * 1024;

  public const string UnreadableMessage =
      "No se pudo leer el archivo enviado: el formulario está incompleto o mal formado. Vuelve a intentarlo.";

  public const string TooLargeMessage = "El archivo es demasiado grande para subirlo.";

  public static string TooLargeFor(long maxFileBytes)
      => $"El archivo es demasiado grande. El tamaño máximo es de {maxFileBytes / 1024 / 1024} MB.";

  /// <summary>Rejects a declared body above the limit and caps an undeclared one, without reading anything.</summary>
  public static void EnsureWithinLimit(HttpRequest request, long maxFileBytes)
  {
    var limit = maxFileBytes + MultipartOverheadBytes;

    if (request.ContentLength > limit)
    {
      throw new PayloadTooLargeException(TooLargeFor(maxFileBytes));
    }

    var bodySize = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (bodySize is { IsReadOnly: false } && (bodySize.MaxRequestBodySize is null || bodySize.MaxRequestBodySize > limit))
    {
      bodySize.MaxRequestBodySize = limit;
    }
  }

  public static async Task<IFormCollection> ReadFormAsync(HttpRequest request, long maxFileBytes, CancellationToken cancellationToken)
  {
    EnsureWithinLimit(request, maxFileBytes);

    try
    {
      return await request.ReadFormAsync(cancellationToken);
    }
    catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
    {
      throw new PayloadTooLargeException(TooLargeFor(maxFileBytes), exception);
    }
    catch (Exception exception) when (exception is InvalidDataException or IOException)
    {
      // BadHttpRequestException is an IOException too: any other reading error of the body lands here.
      throw new BadRequestException(UnreadableMessage, exception);
    }
  }
}
