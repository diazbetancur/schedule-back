using ImageMagick;

namespace Barbershop.Infrastructure.Media;

/// <summary>
/// The real type of an uploaded file, read from its first bytes (BUG-020, BUG-058). The declared Content-Type and the
/// file name extension are chosen by the client, so an HTML page, an SVG or an executable could be sent as image/png;
/// only the signature decides what the file is.
/// </summary>
internal static class FileSignatures
{
  public const int HeaderLength = 12;

  public const string Pdf = "application/pdf";

  /// <summary>Formats browsers show everywhere: stored as they are.</summary>
  public static readonly IReadOnlySet<string> WebImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
  {
    "image/jpeg", "image/png", "image/webp", "image/gif"
  };

  /// <summary>Phone and desktop photo formats browsers do not show everywhere: converted to JPEG.</summary>
  public static readonly IReadOnlySet<string> ConvertibleImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
  {
    "image/heic", "image/avif", "image/bmp", "image/tiff"
  };

  /// <summary>The content type the signature shows, or null when the file is none of the accepted formats.</summary>
  public static string? DetectContentType(ReadOnlySpan<byte> header)
  {
    if (header.StartsWith("%PDF-"u8))
    {
      return Pdf;
    }

    return MagickImageTranscoder.DetectRasterFormat(header) switch
    {
      MagickFormat.Jpeg => "image/jpeg",
      MagickFormat.Png => "image/png",
      MagickFormat.Gif => "image/gif",
      MagickFormat.WebP => "image/webp",
      MagickFormat.Heic => "image/heic",
      MagickFormat.Avif => "image/avif",
      MagickFormat.Bmp => "image/bmp",
      MagickFormat.Tiff => "image/tiff",
      _ => null
    };
  }

  /// <summary>Reads the first bytes of a seekable stream and rewinds it.</summary>
  public static async Task<string?> DetectContentTypeAsync(Stream content, CancellationToken cancellationToken)
  {
    content.Position = 0;
    var header = new byte[HeaderLength];
    var length = 0;
    int read;
    while (length < header.Length && (read = await content.ReadAsync(header.AsMemory(length), cancellationToken)) > 0)
    {
      length += read;
    }

    content.Position = 0;
    return DetectContentType(header.AsSpan(0, length));
  }
}
