using Barbershop.Application.Media;
using ImageMagick;
using Microsoft.Extensions.Logging;

namespace Barbershop.Infrastructure.Media;

/// <summary>
/// Converts phone and desktop raster photos the browsers cannot show everywhere (HEIC/HEIF, AVIF, BMP, TIFF) to JPEG.
/// Only those formats are decoded, recognised by their first bytes and read with that format forced (BUG-078):
/// ImageMagick would otherwise interpret any input it recognises, including SVG and other vector or scripted formats
/// (XML with external references), and an SVG must be rejected like any other non-image upload, not turned into a
/// public image.
/// </summary>
internal sealed class MagickImageTranscoder : IImageTranscoder
{
  private const int HeaderLength = 12;

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

      var format = DetectRasterFormat(buffer.GetBuffer().AsSpan(0, (int)Math.Min(buffer.Length, HeaderLength)));
      if (format is null)
      {
        _logger.LogWarning(
            "Upload with content type {ContentType} is not a raster format that can be converted to JPEG.",
            contentType);
        return null;
      }

      buffer.Position = 0;
      using var image = new MagickImage(buffer, new MagickReadSettings { Format = format.Value });
      image.Format = MagickFormat.Jpeg;

      var output = new MemoryStream();
      image.Write(output);
      output.Position = 0;

      return new TranscodedImage(output, output.Length);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
      _logger.LogWarning(
          exception,
          "Failed to transcode an upload with content type {ContentType} to JPEG.",
          contentType);
      return null;
    }
  }

  /// <summary>The raster format the file's signature shows, or null for anything else (SVG, text, documents...).</summary>
  internal static MagickFormat? DetectRasterFormat(ReadOnlySpan<byte> header)
  {
    if (header.Length >= 12 && header[4..8].SequenceEqual("ftyp"u8))
    {
      // ISO base media file (HEIF family): the major brand says which one.
      var brand = header[8..12];
      if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8))
      {
        return MagickFormat.Avif;
      }

      return brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("hevc"u8)
          || brand.SequenceEqual("hevx"u8) || brand.SequenceEqual("heim"u8) || brand.SequenceEqual("heis"u8)
          || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8)
          ? MagickFormat.Heic
          : null;
    }

    if (header.StartsWith("BM"u8))
    {
      return MagickFormat.Bmp;
    }

    if (header.StartsWith("II*\0"u8) || header.StartsWith("MM\0*"u8))
    {
      return MagickFormat.Tiff;
    }

    if (header.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
    {
      return MagickFormat.Jpeg;
    }

    if (header.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }))
    {
      return MagickFormat.Png;
    }

    if (header.StartsWith("GIF8"u8))
    {
      return MagickFormat.Gif;
    }

    return header.Length >= 12 && header.StartsWith("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8)
        ? MagickFormat.WebP
        : null;
  }
}
