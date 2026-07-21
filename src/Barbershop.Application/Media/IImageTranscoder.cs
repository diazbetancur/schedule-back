namespace Barbershop.Application.Media;

public interface IImageTranscoder
{
  /// <summary>
  /// Attempts to decode <paramref name="content"/> as an image and re-encode it as JPEG.
  /// Returns null if decoding failed - a corrupt file, or a content type that claims
  /// to be an image but isn't. Never throws for bad/corrupt input.
  /// </summary>
  Task<TranscodedImage?> TryConvertToJpegAsync(
      Stream content,
      string contentType,
      CancellationToken cancellationToken = default);
}

public sealed record TranscodedImage(Stream Content, long SizeBytes);
