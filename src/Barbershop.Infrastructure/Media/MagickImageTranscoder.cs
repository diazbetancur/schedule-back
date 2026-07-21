using Barbershop.Application.Media;
using ImageMagick;
using Microsoft.Extensions.Logging;

namespace Barbershop.Infrastructure.Media;

internal sealed class MagickImageTranscoder : IImageTranscoder
{
  private readonly ILogger<MagickImageTranscoder> _logger;

  public MagickImageTranscoder(ILogger<MagickImageTranscoder> logger)
  {
    _logger = logger;
  }

  public async Task<TranscodedImage?> TryConvertToJpegAsync(
      Stream content,
      string contentType,
      CancellationToken cancellationToken = default)
  {
    try
    {
      using var buffer = new MemoryStream();
      await content.CopyToAsync(buffer, cancellationToken);
      buffer.Position = 0;

      using var image = new MagickImage(buffer);
      image.Format = MagickFormat.Jpeg;

      var output = new MemoryStream();
      image.Write(output);
      output.Position = 0;

      return new TranscodedImage(output, output.Length);
    }
    catch (MagickException exception)
    {
      _logger.LogWarning(
          exception,
          "Failed to transcode an upload with content type {ContentType} to JPEG.",
          contentType);
      return null;
    }
  }
}
