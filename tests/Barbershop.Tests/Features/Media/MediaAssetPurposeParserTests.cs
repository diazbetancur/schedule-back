using Barbershop.Application.Media;
using Barbershop.Domain.Media;

namespace Barbershop.Tests.Features.Media;

/// <summary>BUG-098: only the name of a defined purpose is accepted.</summary>
public sealed class MediaAssetPurposeParserTests
{
  [Theory]
  [InlineData("Banner", MediaAssetPurpose.Banner)]
  [InlineData("banner", MediaAssetPurpose.Banner)]
  [InlineData(" TipsQr ", MediaAssetPurpose.TipsQr)]
  [InlineData("CUSTOMERREFERENCE", MediaAssetPurpose.CustomerReference)]
  [InlineData("ProductImage", MediaAssetPurpose.ProductImage)]
  public void TryParse_AcceptsTheNameOfADefinedPurpose(string value, MediaAssetPurpose expected)
  {
    Assert.True(MediaAssetPurposeParser.TryParse(value, out var purpose));
    Assert.Equal(expected, purpose);
  }

  [Theory]
  [InlineData("999")]
  [InlineData("6")]
  [InlineData("-1")]
  [InlineData("0")]
  [InlineData("Logo, Banner")]
  [InlineData("Logo,Banner")]
  [InlineData("Foo")]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData(null)]
  public void TryParse_RejectsNumbersCombinationsAndUnknownNames(string? value)
  {
    Assert.False(MediaAssetPurposeParser.TryParse(value, out _));
  }
}
