using Ariva.Core.Domain.Entities;
using Ariva.Infra.Storage;
using CsCheck;
using FluentAssertions;
using SkiaSharp;

namespace Ariva.UnitTests.Properties;

/// <summary>
/// ARV-142a, CWE-120 and CWE-434 (security review): the advertisement image re-encoder given random bytes and valid PNG,
/// JPEG and WebP images cut short or mutated: each answers with an image of an allowed type within the limits, whose stored
/// bytes decode as that type, or with null; never an exception, and never anything larger than the limits. A failure prints
/// CsCheck's shrunk input and seed.
/// </summary>
public sealed class AdvertisingImagePropertyTests
{
    private static byte[] Sample(SKEncodedImageFormat format, bool lossless = false)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(24, 12, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var x = 0; x < 24; x++)
        for (var y = 0; y < 12; y++)
            bitmap.SetPixel(x, y, new SKColor((byte)(x * 10), (byte)(y * 20), 90, (byte)(200 + y)));
        using var pixmap = bitmap.PeekPixels();
        using var data = format == SKEncodedImageFormat.Webp
            ? pixmap.Encode(new SKWebpEncoderOptions(lossless ? SKWebpEncoderCompression.Lossless : SKWebpEncoderCompression.Lossy, 80))
            : bitmap.Encode(format, 90);
        return data.ToArray();
    }

    private static void Holds(byte[] input)
    {
        var image = AdvertisingImages.ReencodeAsync(input, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        if (image is null)
            return;
        AdvertisingMediaFile.ImageTypes.Should().ContainKey(image.ContentType);
        AdvertisingImages.Fits(image.WidthPixels, image.HeightPixels).Should().BeTrue("never larger than the limits");
        image.Bytes.LongLength.Should().BeInRange(1, AdvertisingMedia.MaxImageBytes);
        using var codec = SKCodec.Create(SKData.CreateCopy(image.Bytes));
        codec.Should().NotBeNull("the stored bytes are an image");
        codec.Info.Width.Should().Be(image.WidthPixels);
        codec.Info.Height.Should().Be(image.HeightPixels);
        AdvertisingImages.ReadHeader(image.Bytes).Should().NotBeNull("and the managed reader reads them back");
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, false)]
    [InlineData(SKEncodedImageFormat.Jpeg, false)]
    [InlineData(SKEncodedImageFormat.Webp, false)]
    [InlineData(SKEncodedImageFormat.Webp, true)]
    public void Reencode_Should_GiveAnImageWithinTheLimitsOrNothing_ForAnyMutationOfAValidImage(SKEncodedImageFormat format, bool lossless) =>
        Fuzz.From(Sample(format, lossless), json: false).Sample(Holds, iter: Fuzz.Iterations, print: b => Convert.ToBase64String(b));
}
