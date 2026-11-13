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

  private const string ScriptedSvg =
      "<?xml version='1.0'?><svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'>"
      + "<script>alert(1)</script><rect width='10' height='10' fill='red'/></svg>";

  /// <summary>BUG-078: an SVG used to be rasterised and published; now it is not decoded at all.</summary>
  [Theory]
  [InlineData("image/svg+xml")]
  [InlineData("image/heic")]
  [InlineData("image/bmp")]
  public async Task TryConvertToJpegAsync_NeverDecodesSvg_WhateverTheDeclaredType(string declaredContentType)
  {
    var transcoder = new MagickImageTranscoder(NullLogger<MagickImageTranscoder>.Instance);
    using var svg = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ScriptedSvg));

    var result = await transcoder.TryConvertToJpegAsync(svg, declaredContentType);

    Assert.Null(result);
  }

  [Theory]
  [InlineData(MagickFormat.Bmp, "image/bmp")]
  [InlineData(MagickFormat.Tiff, "image/tiff")]
  public async Task TryConvertToJpegAsync_ConvertsRasterFormatsBrowsersDoNotShow(MagickFormat format, string contentType)
  {
    var transcoder = new MagickImageTranscoder(NullLogger<MagickImageTranscoder>.Instance);
    using var input = CreateTestImage(format);

    var result = await transcoder.TryConvertToJpegAsync(input, contentType);

    Assert.NotNull(result);
    using var converted = new MagickImage(result!.Content);
    Assert.Equal(MagickFormat.Jpeg, converted.Format);
  }

  [Theory]
  [InlineData(new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c' }, MagickFormat.Heic)]
  [InlineData(new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'i', (byte)'f', (byte)'1' }, MagickFormat.Heic)]
  [InlineData(new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'a', (byte)'v', (byte)'i', (byte)'f' }, MagickFormat.Avif)]
  public void DetectRasterFormat_RecognisesTheHeifFamilyByBrand(byte[] header, MagickFormat expected)
  {
    Assert.Equal(expected, MagickImageTranscoder.DetectRasterFormat(header));
  }

  [Theory]
  [InlineData("<svg xmlns='http://www.w3.org/2000/svg'/>")]
  [InlineData("<?xml version='1.0'?>")]
  [InlineData("%PDF-1.7 anything")]
  [InlineData("push graphic-context")]
  [InlineData("    ftypqt  ")]
  public void DetectRasterFormat_RejectsVectorTextAndDocuments(string text)
  {
    Assert.Null(MagickImageTranscoder.DetectRasterFormat(System.Text.Encoding.ASCII.GetBytes(text)));
  }

  private static MemoryStream CreateTestImage(MagickFormat format)
  {
    using var image = new MagickImage(MagickColors.Tomato, 4, 4);
    var stream = new MemoryStream();
    image.Format = format;
    image.Write(stream);
    stream.Position = 0;
    return stream;
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
