using System.Buffers.Binary;
using Ariva.Core.Domain.Entities;
using SkiaSharp;

namespace Ariva.Infra.Storage;

/// <summary>An advertisement image as written anew: its type, the bytes to store and its size in pixels.</summary>
public sealed record ReencodedImage(string ContentType, string Extension, byte[] Bytes, int WidthPixels, int HeightPixels);

/// <summary>
/// Takes advertisement images (ARV-142a, CWE-434, CWE-79, CWE-400, CWE-120). The type comes from the bytes, never from the
/// name or the declared content type. Before any native code sees the file, managed code reads its header
/// (<see cref="ReadHeader"/>): a PNG, JPEG or WebP signature, the size in pixels, and whether it is animated (an APNG's
/// <c>acTL</c> chunk, a WebP's animation flag); a file without a readable header, an animated one, one larger than
/// <see cref="AdvertisingMedia.MaxSide"/> a side or <see cref="AdvertisingMedia.MaxPixels"/>, and a JPEG with more than
/// <see cref="MaxJpegScans"/> scans are refused there. The decoder must then read the same format and size, and decode the
/// whole image; a truncated one is refused. What is kept is the decoded picture only, turned upright from its EXIF
/// orientation, converted to sRGB and encoded again in its own format: no metadata (EXIF, XMP, comments, text chunks, ICC
/// profiles) and nothing after the image's end is carried over, so the page or script of a polyglot is gone. The new
/// compressed data is still made from pixels the uploader chose, which can be crafted to spell bytes of their choosing, so
/// re-encoding is not the control against content sniffing: serving each file with its own type, <c>nosniff</c> and a
/// sandboxing content security policy is. One image is decoded at a time per process (<see cref="MaxConcurrentDecodes"/>),
/// with the encoders' effort kept low: a 3,840 by 2,160 image of noise takes at most a few seconds (security review of ARV-142a).
/// </summary>
public static class AdvertisingImages
{
    public const int MaxConcurrentDecodes = 1;

    /// <summary>
    /// The most scans a JPEG may have (a progressive photograph has about ten): each scan is decoded over the whole image, so
    /// thousands of them keep a decoder busy for minutes on a small file (CWE-400).
    /// </summary>
    public const int MaxJpegScans = 64;

    private const int JpegQuality = 90;

    /// <summary>A lossy WebP's quality.</summary>
    private const float WebpQuality = 85f;

    /// <summary>A lossless WebP's effort (libwebp's quality in lossless mode): 25 is several times faster than the default 75.</summary>
    private const float WebpLosslessEffort = 25f;

    /// <summary>PNG: one cheap filter and a middle compression level, a fraction of the time of every filter at level 6.</summary>
    private const SKPngEncoderFilterFlags PngFilters = SKPngEncoderFilterFlags.Sub;

    private const int PngZlibLevel = 4;

    private static readonly SemaphoreSlim Decoding = new(MaxConcurrentDecodes, MaxConcurrentDecodes);

    /// <summary>The image written anew, or null when the bytes are not a PNG, JPEG or WebP image within the limits.</summary>
    public static async Task<ReencodedImage> ReencodeAsync(byte[] bytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        await Decoding.WaitAsync(ct);
        try
        {
            return Reencode(bytes);
        }
        finally
        {
            Decoding.Release();
        }
    }

    /// <summary>The format the signature names: PNG, JPEG or WebP, or null for anything else.</summary>
    public static SKEncodedImageFormat? Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return SKEncodedImageFormat.Png;
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return SKEncodedImageFormat.Jpeg;
        if (bytes.Length >= 16 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
            return SKEncodedImageFormat.Webp;
        return null;
    }

    private static ReencodedImage Reencode(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.LongLength > AdvertisingMedia.MaxImageBytes || ReadHeader(bytes) is not { Animated: false } header ||
            !Fits(header.Width, header.Height))
            return null;
        var format = header.Format;
        if (format == SKEncodedImageFormat.Jpeg && JpegScans(bytes) > MaxJpegScans)
            return null;

        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        // The decoder must read what the managed header said: the same format, size and a single frame.
        if (codec is null || codec.EncodedFormat != format || codec.FrameCount > 1)
            return null;
        var source = codec.Info;
        if (source.Width != header.Width || source.Height != header.Height)
            return null;

        var target = new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888,
            source.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul, SKColorSpace.CreateSrgb());
        using var decoded = new SKBitmap();
        if (!decoded.TryAllocPixels(target) || codec.GetPixels(target, decoded.GetPixels()) != SKCodecResult.Success)
            return null;

        // Turned upright only when the EXIF orientation says so, so a plain image is never copied (a copy is 33 MB at the most:
        // 8,294,400 pixels of 4 bytes, beside the decoded bitmap of the same size).
        var origin = codec.EncodedOrigin;
        using var turned = origin is SKEncodedOrigin.TopLeft ? null : Upright(decoded, origin);
        if (origin is not SKEncodedOrigin.TopLeft && turned is null)
            return null;
        var upright = turned ?? decoded;
        using var pixmap = upright.PeekPixels();
        using var encoded = format switch
        {
            SKEncodedImageFormat.Png => pixmap.Encode(new SKPngEncoderOptions(PngFilters, PngZlibLevel)),
            SKEncodedImageFormat.Jpeg => pixmap.Encode(new SKJpegEncoderOptions(JpegQuality)),
            _ => pixmap.Encode(header.Lossless
                ? new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, WebpLosslessEffort)
                : new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, WebpQuality))
        };
        if (encoded is null || encoded.Size == 0 || encoded.Size > AdvertisingMedia.MaxImageBytes)
            return null;

        var (contentType, extension) = format switch
        {
            SKEncodedImageFormat.Png => ("image/png", "png"),
            SKEncodedImageFormat.Jpeg => ("image/jpeg", "jpg"),
            _ => ("image/webp", "webp")
        };
        return new ReencodedImage(contentType, extension, encoded.ToArray(), upright.Width, upright.Height);
    }

    public static bool Fits(int width, int height) =>
        width is > 0 and <= AdvertisingMedia.MaxSide && height is > 0 and <= AdvertisingMedia.MaxSide && (long)width * height <= AdvertisingMedia.MaxPixels;

    /// <summary>
    /// The picture as it is meant to be seen, for an EXIF orientation other than the default: the orientation itself is
    /// metadata and is not written again. A new bitmap, which the caller disposes; null when it cannot be allocated.
    /// </summary>
    private static SKBitmap Upright(SKBitmap decoded, SKEncodedOrigin origin)
    {
        var (w, h) = (decoded.Width, decoded.Height);
        var swapped = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var info = decoded.Info.WithSize(swapped ? h : w, swapped ? w : h);
        var upright = new SKBitmap();
        if (!upright.TryAllocPixels(info))
        {
            upright.Dispose();
            return null;
        }

        using (var canvas = new SKCanvas(upright))
        {
            canvas.Clear(SKColors.Transparent);
            // Each matrix maps the stored picture onto the upright one (EXIF orientations 2 to 8).
            var matrix = origin switch
            {
                SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
                SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
                SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
                SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
                SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
                SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
                _ => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1)
            };
            canvas.SetMatrix(matrix);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            decoded.SetImmutable();
            using var image = SKImage.FromBitmap(decoded);
            // Whole pixels moved, never resampled: every orientation maps the grid onto itself.
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }

        return upright;
    }

    /// <summary>
    /// The start-of-scan markers (FF DA) in a JPEG. Entropy-coded data never holds FF followed by anything but 00 or a restart
    /// marker, so every FF DA pair is a scan, or a byte pair inside a header segment, which only counts against the file.
    /// </summary>
    public static int JpegScans(ReadOnlySpan<byte> bytes)
    {
        var scans = 0;
        for (var i = 0; i + 1 < bytes.Length; i++)
        {
            if (bytes[i] == 0xFF && bytes[i + 1] == 0xDA)
                scans++;
        }

        return scans;
    }

    /// <summary>What managed code reads of an image before any native code sees it.</summary>
    public sealed record ImageHeader(SKEncodedImageFormat Format, int Width, int Height, bool Animated, bool Lossless);

    /// <summary>
    /// The header of a PNG, JPEG or WebP read in managed code, or null when the bytes are none of them or their header cannot
    /// be read: a PNG's IHDR (the first chunk) and every chunk before its first IDAT, an <c>acTL</c> among them marking an
    /// animated PNG; a JPEG's segments up to its first start-of-frame; a WebP's RIFF size and its first chunk (VP8, VP8L, or
    /// VP8X with its animation flag and canvas). Every length is checked against what is left before it is used.
    /// </summary>
    public static ImageHeader ReadHeader(ReadOnlySpan<byte> bytes) =>
        Sniff(bytes) switch
        {
            SKEncodedImageFormat.Png => PngHeader(bytes),
            SKEncodedImageFormat.Jpeg => JpegHeader(bytes),
            SKEncodedImageFormat.Webp => WebpHeader(bytes),
            _ => null
        };

    private static ImageHeader PngHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 33 || BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != 13 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
            return null;
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]);
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
            return null;
        var animated = false;
        var offset = 33L;
        while (offset + 8 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[(int)offset..]);
            var type = bytes.Slice((int)offset + 4, 4);
            if (type.SequenceEqual("IDAT"u8))
                break;
            if (type.SequenceEqual("acTL"u8))
                animated = true;
            offset += 12L + length;
        }

        return offset + 8 <= bytes.Length ? new ImageHeader(SKEncodedImageFormat.Png, (int)width, (int)height, animated, true) : null;
    }

    private static ImageHeader JpegHeader(ReadOnlySpan<byte> bytes)
    {
        var offset = 2;
        while (offset + 4 <= bytes.Length)
        {
            if (bytes[offset] != 0xFF)
                return null;
            var marker = bytes[offset + 1];
            if (marker == 0xFF)
            {
                offset++;
                continue;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 2)..]);
            if (length < 2 || marker == 0xDA)
                return null;
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (offset + 9 > bytes.Length || length < 7)
                    return null;
                var height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 7)..]);
                return width == 0 || height == 0 ? null : new ImageHeader(SKEncodedImageFormat.Jpeg, width, height, false, false);
            }

            offset += 2 + length;
        }

        return null;
    }

    private static ImageHeader WebpHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 30 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) > (uint)(bytes.Length - 8))
            return null;
        var chunk = bytes.Slice(12, 4);
        if (chunk.SequenceEqual("VP8 "u8))
        {
            // A key frame's start code, then 14-bit width and height.
            if (!bytes.Slice(23, 3).SequenceEqual(new byte[] { 0x9D, 0x01, 0x2A }))
                return null;
            var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..]) & 0x3FFF;
            var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]) & 0x3FFF;
            return width == 0 || height == 0 ? null : new ImageHeader(SKEncodedImageFormat.Webp, width, height, false, false);
        }

        if (chunk.SequenceEqual("VP8L"u8))
        {
            // The signature byte, then 14 bits of width less one and 14 bits of height less one.
            if (bytes[20] != 0x2F)
                return null;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]);
            return new ImageHeader(SKEncodedImageFormat.Webp, (int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1, false, true);
        }

        if (chunk.SequenceEqual("VP8X"u8))
        {
            var animated = (bytes[20] & 0x02) != 0;
            var width = (bytes[24] | (bytes[25] << 8) | (bytes[26] << 16)) + 1;
            var height = (bytes[27] | (bytes[28] << 8) | (bytes[29] << 16)) + 1;
            return new ImageHeader(SKEncodedImageFormat.Webp, width, height, animated, LosslessAfterExtendedHeader(bytes));
        }

        return null;
    }

    /// <summary>Whether the first image chunk after an extended header is lossless (VP8L), so the WebP stays lossless.</summary>
    private static bool LosslessAfterExtendedHeader(ReadOnlySpan<byte> bytes)
    {
        var offset = 12L;
        while (offset + 8 <= bytes.Length)
        {
            var chunk = bytes.Slice((int)offset, 4);
            if (chunk.SequenceEqual("VP8L"u8))
                return true;
            if (chunk.SequenceEqual("VP8 "u8))
                return false;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[((int)offset + 4)..]);
            offset += 8L + size + (size & 1);
        }

        return false;
    }
}
