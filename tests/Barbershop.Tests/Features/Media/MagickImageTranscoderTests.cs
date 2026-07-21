using Barbershop.Infrastructure.Media;
using ImageMagick;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbershop.Tests.Features.Media;

public sealed class MagickImageTranscoderTests
{
  [Fact]
  public async Task TryConvertToJpegAsync_DecodesAndReencodesAsJpeg()
  {
    var transcoder = new MagickImageTranscoder(NullLogger<MagickImageTranscoder>.Instance);
    using var pngStream = CreateTestPng();

    var result = await transcoder.TryConvertToJpegAsync(pngStream, "image/png");

    Assert.NotNull(result);
    Assert.True(result!.SizeBytes > 0);

    var header = new byte[2];
    var read = await result.Content.ReadAsync(header);
    Assert.Equal(2, read);
    Assert.Equal(0xFF, header[0]); // JPEG magic bytes: FF D8
    Assert.Equal(0xD8, header[1]);
    await result.Content.DisposeAsync();
  }

  [Fact]
  public async Task TryConvertToJpegAsync_ReturnsNullForCorruptData()
  {
    var transcoder = new MagickImageTranscoder(NullLogger<MagickImageTranscoder>.Instance);
    using var garbage = new MemoryStream([0x00, 0x01, 0x02, 0x03]);

    var result = await transcoder.TryConvertToJpegAsync(garbage, "image/heic");

    Assert.Null(result);
  }

  private static MemoryStream CreateTestPng()
  {
    using var image = new MagickImage(MagickColors.Tomato, 4, 4);
    var stream = new MemoryStream();
    image.Format = MagickFormat.Png;
    image.Write(stream);
    stream.Position = 0;
    return stream;
  }
}
