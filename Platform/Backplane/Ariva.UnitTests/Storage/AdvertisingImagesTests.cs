using System.Buffers.Binary;
using System.Text;
using Ariva.Core.Domain.Entities;
using Ariva.Infra.Storage;
using FluentAssertions;
using SkiaSharp;

namespace Ariva.UnitTests.Storage;

/// <summary>
/// ARV-142a: advertisement images are typed by their bytes and written anew. Their header is read in managed code before
/// any native code sees them; only the picture survives: text chunks, comments, EXIF and anything after the image's end are
/// gone, and the EXIF orientation is applied first. SVG, HTML, PDF, GIF, video, animated (APNG and WebP), truncated and
/// oversized images are refused, the size read from the header before anything is decoded, and a 4K image of noise is
/// written anew within a time budget.
/// </summary>
public sealed class AdvertisingImagesTests
{
    private const string Markup = "<html><script>alert(document.domain)</script></html>";

    /// <summary>A picture whose left half is red and right half blue, so its orientation can be read back.</summary>
    private static SKBitmap Picture(int width = 16, int height = 8)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var x = 0; x < width; x++)
        for (var y = 0; y < height; y++)
            bitmap.SetPixel(x, y, x < width / 2 ? SKColors.Red : SKColors.Blue);
        return bitmap;
    }

    private static byte[] Encode(SKEncodedImageFormat format, int width = 16, int height = 8, bool lossless = false)
    {
        using var bitmap = Picture(width, height);
        using var pixmap = bitmap.PeekPixels();
        using var data = format == SKEncodedImageFormat.Webp
            ? pixmap.Encode(new SKWebpEncoderOptions(lossless ? SKWebpEncoderCompression.Lossless : SKWebpEncoderCompression.Lossy, 90))
            : bitmap.Encode(format, 95);
        return data.ToArray();
    }

    private static Task<ReencodedImage> Reencode(byte[] bytes) => AdvertisingImages.ReencodeAsync(bytes, TestContext.Current.CancellationToken);

    private static bool Contains(byte[] bytes, string text) => Encoding.Latin1.GetString(bytes).Contains(text, StringComparison.Ordinal);

    /// <summary>A PNG chunk with its CRC.</summary>
    private static byte[] Chunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return ~crc;
    }

    /// <summary>The PNG with a chunk inserted before IEND.</summary>
    private static byte[] WithChunkBeforeEnd(byte[] png, byte[] chunk)
    {
        var end = png.Length - 12;
        return [.. png.AsSpan(0, end), .. chunk, .. png.AsSpan(end)];
    }

    /// <summary>The JPEG with a segment inserted after its start-of-image marker.</summary>
    private static byte[] WithSegment(byte[] jpeg, byte marker, byte[] payload)
    {
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment, 4);
        return [.. jpeg.AsSpan(0, 2), .. segment, .. jpeg.AsSpan(2)];
    }

    /// <summary>An EXIF segment payload holding only an orientation.</summary>
    private static byte[] ExifOrientation(ushort orientation)
    {
        var tiff = new byte[26];
        "II*\0"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), 0x0112);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(18), orientation);
        return [.. "Exif\0\0"u8, .. tiff];
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, "image/png", "png")]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg", "jpg")]
    [InlineData(SKEncodedImageFormat.Webp, "image/webp", "webp")]
    public async Task Reencode_Should_WriteTheImageAnewInItsOwnType_When_ItIsPngJpegOrWebp(SKEncodedImageFormat format, string contentType, string extension)
    {
        var image = await Reencode(Encode(format));

        image.Should().NotBeNull();
        (image.ContentType, image.Extension, image.WidthPixels, image.HeightPixels).Should().Be((contentType, extension, 16, 8));
        using var codec = SKCodec.Create(SKData.CreateCopy(image.Bytes));
        codec.EncodedFormat.Should().Be(format, "the stored bytes are the type they are served as");
    }

    [Fact]
    public async Task Reencode_Should_KeepOnlyThePicture_When_APngCarriesTextChunksAndMarkupAfterItsEnd()
    {
        var polyglot = WithChunkBeforeEnd(Encode(SKEncodedImageFormat.Png), Chunk("tEXt", Encoding.Latin1.GetBytes("Comment\0" + Markup)));
        polyglot = [.. polyglot, .. Encoding.ASCII.GetBytes(Markup)];
        Contains(polyglot, "<script>").Should().BeTrue("the attack file carries markup");

        var image = await Reencode(polyglot);

        image.Should().NotBeNull("the picture itself is a valid PNG");
        Contains(image.Bytes, "<script").Should().BeFalse();
        Contains(image.Bytes, "alert").Should().BeFalse();
        Contains(image.Bytes, "tEXt").Should().BeFalse("no text chunk is written");
    }

    [Fact]
    public async Task Reencode_Should_DropCommentsAndExif_When_AJpegCarriesThem()
    {
        var jpeg = WithSegment(Encode(SKEncodedImageFormat.Jpeg), 0xFE, Encoding.ASCII.GetBytes(Markup));
        jpeg = WithSegment(jpeg, 0xE1, [.. "Exif\0\0"u8, .. Encoding.ASCII.GetBytes("<x:xmpmeta>" + Markup)]);
        jpeg = [.. jpeg, .. Encoding.ASCII.GetBytes(Markup)];

        var image = await Reencode(jpeg);

        image.Should().NotBeNull();
        Contains(image.Bytes, "<script").Should().BeFalse();
        Contains(image.Bytes, "xmpmeta").Should().BeFalse();
        Contains(image.Bytes, "Exif").Should().BeFalse("no EXIF is written again");
    }

    [Fact]
    public async Task Reencode_Should_DropMarkupAfterTheRiff_When_AWebpCarriesIt()
    {
        var webp = Encode(SKEncodedImageFormat.Webp, lossless: true);

        var image = await Reencode([.. webp, .. Encoding.ASCII.GetBytes(Markup)]);

        image.Should().NotBeNull();
        Contains(image.Bytes, "<script").Should().BeFalse();
        Contains(image.Bytes, "VP8L").Should().BeTrue("a lossless WebP stays lossless");
    }

    [Fact]
    public async Task Reencode_Should_TurnTheImageUpright_When_ItsExifOrientationSaysSo()
    {
        // Orientation 6 (RightTop): the stored picture is shown turned a quarter clockwise, so its left half ends on top.
        var image = await Reencode(WithSegment(Encode(SKEncodedImageFormat.Jpeg), 0xE1, ExifOrientation(6)));

        image.Should().NotBeNull();
        (image.WidthPixels, image.HeightPixels).Should().Be((8, 16));
        using var bitmap = SKBitmap.Decode(image.Bytes);
        var top = bitmap.GetPixel(4, 3);
        var bottom = bitmap.GetPixel(4, 12);
        top.Red.Should().BeGreaterThan(200, "the red half is on top");
        top.Blue.Should().BeLessThan(80);
        bottom.Blue.Should().BeGreaterThan(200, "the blue half is below");
        bottom.Red.Should().BeLessThan(80);
        Contains(image.Bytes, "Exif").Should().BeFalse();
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>", "SVG")]
    [InlineData("<html><body><script>alert(1)</script></body></html>", "HTML")]
    [InlineData("%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj", "PDF")]
    [InlineData("GIF89a\u0001\u0000\u0001\u0000\u0000\u0000\u0000;", "GIF")]
    [InlineData("BM\u001e\u0000\u0000\u0000", "BMP")]
    [InlineData("\u0000\u0000\u0000\u0018ftypisom\u0000\u0000\u0002\u0000isomiso2", "MP4")]
    [InlineData("\u001aE\u00df\u00a3\u009fB\u0086\u0081\u0001webm", "WebM")]
    [InlineData("RIFF\u0010\u0000\u0000\u0000WEBPVP8 junkjunk", "a WebP header over junk")]
    [InlineData("", "nothing")]
    public async Task Reencode_Should_Refuse_When_TheBytesAreNotAPngJpegOrWebpImage(string content, string reason) =>
        (await Reencode(Encoding.Latin1.GetBytes(content))).Should().BeNull(reason);

    [Fact]
    public async Task Reencode_Should_Refuse_When_TheSignatureAndTheFormatDisagree()
    {
        var jpeg = Encode(SKEncodedImageFormat.Jpeg);
        byte[] pngSignatureOverJpeg = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. jpeg];

        (await Reencode(pngSignatureOverJpeg)).Should().BeNull();
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Webp)]
    public async Task Reencode_Should_Refuse_When_TheImageIsTruncated(SKEncodedImageFormat format)
    {
        var whole = Encode(format, 64, 64);

        (await Reencode(whole[..(whole.Length / 2)])).Should().BeNull("an image that cannot be read whole is refused");
    }

    [Fact]
    public async Task Reencode_Should_RefuseFromTheHeader_When_APngClaimsMorePixelsThanTheLimit()
    {
        // A 50,000 by 50,000 header over one tiny data chunk: 10 GB once decoded, refused before any native code reads it.
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, 50_000);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 50_000);
        header[8] = 8;
        header[9] = 6;
        byte[] bomb = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", header), .. Chunk("IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]), .. Chunk("IEND", [])];

        AdvertisingImages.ReadHeader(bomb).Should().Match<AdvertisingImages.ImageHeader>(h => h.Width == 50_000 && h.Height == 50_000, "read in managed code");
        (await Reencode(bomb)).Should().BeNull();
    }

    [Theory]
    [InlineData(AdvertisingMedia.MaxSide + 1, 8, "a side over the limit")]
    [InlineData(3840, 2161, "over 8,294,400 pixels")]
    public async Task Reencode_Should_Refuse_When_TheImageIsLargerThanTheLimits(int width, int height, string reason) =>
        (await Reencode(Encode(SKEncodedImageFormat.Png, width, height))).Should().BeNull(reason);

    [Fact]
    public async Task Reencode_Should_Refuse_When_TheFileIsLargerThanTenMegabytes()
    {
        byte[] large = [.. Encode(SKEncodedImageFormat.Png), .. new byte[AdvertisingMedia.MaxImageBytes]];

        (await Reencode(large)).Should().BeNull();
    }

    [Fact]
    public async Task Reencode_Should_Refuse_When_AJpegHasMoreScansThanTheLimit()
    {
        var jpeg = Encode(SKEncodedImageFormat.Jpeg);
        AdvertisingImages.JpegScans(jpeg).Should().Be(1, "a baseline JPEG has one scan");
        byte[] markers = [.. Enumerable.Repeat<byte[]>([0xFF, 0xDA], AdvertisingImages.MaxJpegScans).SelectMany(m => m)];

        (await Reencode(WithSegment(jpeg, 0xFE, markers))).Should().BeNull("65 start-of-scan markers");
        (await Reencode(WithSegment(jpeg, 0xFE, markers[..^2]))).Should().NotBeNull("64 are the limit");
    }

    [Fact]
    public async Task Reencode_Should_Refuse_When_AWebpIsAnimated()
    {
        // Two frames of a 1 by 1 lossless picture in an animated container (VP8X with the animation flag, ANIM, two ANMF).
        var still = Encode(SKEncodedImageFormat.Webp, 1, 1, lossless: true);
        var size = BinaryPrimitives.ReadInt32LittleEndian(still.AsSpan(16));
        var vp8L = still[12..(12 + 8 + size + (size & 1))];
        byte[] RiffChunk(string type, byte[] data) =>
            [.. Encoding.ASCII.GetBytes(type), .. BitConverter.GetBytes(data.Length), .. data, .. (data.Length % 2 == 1 ? new byte[1] : [])];
        byte[] frame = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 100, 0, 0, 0, .. vp8L];
        byte[] body =
        [
            .. "WEBP"u8,
            .. RiffChunk("VP8X", [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
            .. RiffChunk("ANIM", [0, 0, 0, 0, 0, 0]),
            .. RiffChunk("ANMF", frame),
            .. RiffChunk("ANMF", frame)
        ];
        byte[] animated = [.. "RIFF"u8, .. BitConverter.GetBytes(body.Length), .. body];
        using (var codec = SKCodec.Create(SKData.CreateCopy(animated)))
        {
            codec.Should().NotBeNull("the test file is an animation the decoder reads");
            codec.FrameCount.Should().Be(2);
        }

        AdvertisingImages.ReadHeader(animated).Should().Match<AdvertisingImages.ImageHeader>(h => h.Animated, "refused from its header, before the decoder sees it");
        (await Reencode(animated)).Should().BeNull("an animated image is refused, not cut to one frame");
    }

    [Fact]
    public async Task Reencode_Should_Refuse_When_APngIsAnimated()
    {
        // An acTL chunk (two frames, played forever) right after IHDR: an animated PNG, which the decoder would cut to one frame.
        var png = Encode(SKEncodedImageFormat.Png);
        var actl = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(actl, 2);
        byte[] apng = [.. png.AsSpan(0, 33), .. Chunk("acTL", actl), .. png.AsSpan(33)];

        AdvertisingImages.ReadHeader(apng).Should().Match<AdvertisingImages.ImageHeader>(h => h.Animated);
        (await Reencode(apng)).Should().BeNull("an animated image is refused, not cut to one frame");
        AdvertisingImages.ReadHeader(png).Should().Match<AdvertisingImages.ImageHeader>(h => !h.Animated);
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, false, true)]
    [InlineData(SKEncodedImageFormat.Jpeg, false, false)]
    [InlineData(SKEncodedImageFormat.Webp, false, false)]
    [InlineData(SKEncodedImageFormat.Webp, true, true)]
    public void ReadHeader_Should_ReadTheFormatAndSizeInManagedCode_When_TheImageIsPngJpegOrWebp(SKEncodedImageFormat format, bool lossless, bool expectLossless)
    {
        var header = AdvertisingImages.ReadHeader(Encode(format, 37, 21, lossless));

        header.Should().Be(new AdvertisingImages.ImageHeader(format, 37, 21, false, expectLossless));
    }

    [Fact]
    public void ReadHeader_Should_ReadTheCanvas_When_AWebpHasAnExtendedHeader()
    {
        // A lossy WebP with transparency is written as VP8X, ALPH and VP8.
        using var bitmap = new SKBitmap(new SKImageInfo(30, 12, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(new SKColor(10, 200, 30, 120));
        using var pixmap = bitmap.PeekPixels();
        using var data = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, 80));
        var webp = data.ToArray();
        Encoding.ASCII.GetString(webp, 12, 4).Should().Be("VP8X");

        AdvertisingImages.ReadHeader(webp).Should().Be(new AdvertisingImages.ImageHeader(SKEncodedImageFormat.Webp, 30, 12, false, false));
    }

    [Theory]
    [InlineData("a PNG whose first chunk is not IHDR")]
    [InlineData("a PNG with no IDAT")]
    [InlineData("a JPEG with no frame before its scan")]
    [InlineData("a WebP whose RIFF size is larger than the file")]
    public void ReadHeader_Should_ReadNothing_When_TheHeaderIsBroken(string reason)
    {
        var png = Encode(SKEncodedImageFormat.Png);
        var jpeg = Encode(SKEncodedImageFormat.Jpeg);
        var webp = Encode(SKEncodedImageFormat.Webp, lossless: true);
        byte[] broken = reason switch
        {
            "a PNG whose first chunk is not IHDR" => [.. png.AsSpan(0, 12), .. "IHDX"u8, .. png.AsSpan(16)],
            "a PNG with no IDAT" => [.. png.AsSpan(0, 33), .. Chunk("IEND", [])],
            "a JPEG with no frame before its scan" => [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x08, 1, 2, 3, 4, 5, 6, .. jpeg.AsSpan(2)],
            _ => [.. webp.AsSpan(0, 4), .. BitConverter.GetBytes(webp.Length * 2), .. webp.AsSpan(8)]
        };

        AdvertisingImages.ReadHeader(broken).Should().BeNull(reason);
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, false)]
    [InlineData(SKEncodedImageFormat.Webp, true)]
    [InlineData(SKEncodedImageFormat.Webp, false)]
    [InlineData(SKEncodedImageFormat.Jpeg, false)]
    public async Task Reencode_Should_StayWithinItsTimeBudget_When_A4kImageOfNoiseIsUploaded(SKEncodedImageFormat format, bool lossless)
    {
        // Among the costliest images within the limits: 3,840 by 2,160 of random grey pixels (16 levels, so every format
        // still fits 10 MB), which no encoder compresses well. Measured on the development container (one decode at a
        // time): at most a few seconds each; the budget is several times that, so a slower machine passes and an encoder
        // setting gone back to its costliest does not.
        // A fixed xorshift sequence: the same noise on every run, without a general-purpose random generator.
        var state = 142u;
        using var bitmap = new SKBitmap(new SKImageInfo(3840, 2160, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var pixels = bitmap.GetPixelSpan();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            var grey = (byte)((state & 15) * 17);
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (grey, grey, grey, 255);
        }

        using var pixmap = bitmap.PeekPixels();
        using var encoded = format switch
        {
            SKEncodedImageFormat.Png => pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.NoFilters, 1)),
            SKEncodedImageFormat.Jpeg => pixmap.Encode(new SKJpegEncoderOptions(90)),
            _ => pixmap.Encode(new SKWebpEncoderOptions(lossless ? SKWebpEncoderCompression.Lossless : SKWebpEncoderCompression.Lossy, lossless ? 0 : 75))
        };
        var bytes = encoded.ToArray();
        bytes.LongLength.Should().BeLessThanOrEqualTo(AdvertisingMedia.MaxImageBytes, "the test image is one the upload takes");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var image = await Reencode(bytes);
        clock.Stop();

        image.Should().NotBeNull();
        TestContext.Current.TestOutputHelper?.WriteLine($"{format}{(lossless ? " lossless" : "")}: {bytes.Length:N0} bytes written anew in {clock.Elapsed.TotalSeconds:F2} s");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), $"{format}{(lossless ? " lossless" : "")} took {clock.Elapsed.TotalSeconds:F1} s");
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(3840, 2160, true)]
    [InlineData(2160, 3840, true)]
    [InlineData(AdvertisingMedia.MaxSide + 1, 1, false)]
    [InlineData(3840, 2161, false)]
    [InlineData(2880, 2880, true)]
    [InlineData(2881, 2880, false)]
    [InlineData(0, 10, false)]
    [InlineData(10, -1, false)]
    public void Fits_Should_HoldTheSideAndPixelLimits_When_Checked(int width, int height, bool expected) =>
        AdvertisingImages.Fits(width, height).Should().Be(expected);
}
