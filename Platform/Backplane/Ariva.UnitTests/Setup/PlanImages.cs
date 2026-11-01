using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SkiaSharp;

namespace Ariva.UnitTests.Setup;

/// <summary>
/// Raster floor plans for the ARV-201 tests: real PNG and JPEG images from SkiaSharp, and the same images with the metadata
/// cameras, editors and CAD exports write put in (EXIF with a position, XMP, text chunks and comments, a colour profile, an
/// animation chunk, a private chunk and a page after the image's end), the text that metadata carries, and readers for the
/// chunks and segments of a result. One source for Ariva.UnitTests and Ariva.IntegrationTests (linked into the second), so
/// the inspector's tests and the upload and bundle import tests use the same files.
/// </summary>
public static class PlanImages
{
    #region Fields

    /// <summary>Text only the inserted metadata carries: a stored plan holds none of it.</summary>
    public static readonly string[] Markers =
    [
        "Exif", "GPSLatitude", "31.9539N", "x:xmpmeta", "ns.adobe.com/xap", "Plan author", "CadExport", "ICC_PROFILE", "acTL",
        "prVt", "<script>", "Trailing page"
    ];

    private const string Script = "<script>alert(document.domain)</script>";

    #endregion

    #region Images

    /// <summary>A picture of a gradient with a red left half, so its pixels and orientation can be compared.</summary>
    public static SKBitmap Picture(int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var x = 0; x < width; x++)
        for (var y = 0; y < height; y++)
            bitmap.SetPixel(x, y, x < width / 2 ? SKColors.Red : new SKColor((byte)(x * 255 / width), (byte)(y * 255 / height), 160));
        return bitmap;
    }

    /// <summary>The picture as SkiaSharp writes a PNG.</summary>
    public static byte[] Png(int width, int height) => Encode(SKEncodedImageFormat.Png, width, height);

    /// <summary>The picture as SkiaSharp writes a JPEG (quality 90).</summary>
    public static byte[] Jpeg(int width, int height) => Encode(SKEncodedImageFormat.Jpeg, width, height);

    /// <summary>
    /// The PNG with, after its IHDR, text (<c>tEXt</c>, <c>zTXt</c>, <c>iTXt</c> holding XMP), <c>eXIf</c>, <c>tIME</c>,
    /// <c>pHYs</c>, <c>iCCP</c>, an animation control chunk and a private chunk; a comment with markup between its last
    /// IDAT and IEND; and a page after IEND.
    /// </summary>
    public static byte[] PngWithMetadata(int width, int height)
    {
        var png = Png(width, height);
        byte[] ahead =
        [
            .. Chunk("tEXt", Latin1("Author\0Plan author")),
            .. Chunk("zTXt", [.. Latin1("Software\0\0"), .. Deflated("CadExport 7.2")]),
            .. Chunk("iTXt", [.. Latin1("XML:com.adobe.xmp\0\0\0\0\0"), .. Encoding.UTF8.GetBytes(Xmp)]),
            .. Chunk("eXIf", Exif),
            .. Chunk("tIME", [0x07, 0xEA, 11, 1, 9, 30, 0]),
            .. Chunk("pHYs", [0, 0, 0x0B, 0x13, 0, 0, 0x0B, 0x13, 1]),
            .. Chunk("iCCP", [.. Latin1("ICC_PROFILE CadExport\0\0"), .. Deflated("not a profile")]),
            .. Chunk("acTL", [0, 0, 0, 1, 0, 0, 0, 0]),
            .. Chunk("prVt", Latin1("private CadExport data"))
        ];
        return InsertPng(png, ahead, Chunk("tEXt", Latin1("Comment\0" + Script)), Latin1("Trailing page <html>" + Script + "</html>"));
    }

    /// <summary>
    /// The JPEG with, after its start-of-image marker, an EXIF APP1 (with a position), an XMP APP1, an ICC profile APP2
    /// and a comment; another comment with markup before its end-of-image marker; and a page after that marker.
    /// </summary>
    public static byte[] JpegWithMetadata(int width, int height)
    {
        var jpeg = Jpeg(width, height);
        byte[] ahead =
        [
            .. Segment(0xE1, Exif),
            .. Segment(0xE1, [.. Latin1("http://ns.adobe.com/xap/1.0/\0"), .. Encoding.UTF8.GetBytes(Xmp)]),
            .. Segment(0xE2, [.. Latin1("ICC_PROFILE\0"), 1, 1, .. Latin1("CadExport profile")]),
            .. Segment(0xFE, Latin1("Plan author, CadExport 7.2"))
        ];
        return InsertJpeg(jpeg, ahead, Segment(0xFE, Latin1(Script)), Latin1("Trailing page <html>" + Script + "</html>"));
    }

    /// <summary>The PNG with chunks after IHDR, chunks before IEND and bytes after IEND.</summary>
    public static byte[] InsertPng(byte[] png, byte[] afterHeader, byte[] beforeEnd = null, byte[] afterEnd = null)
    {
        const int header = 8 + 25;
        var end = png.Length - 12;
        return [.. png.AsSpan(0, header), .. afterHeader, .. png.AsSpan(header, end - header), .. beforeEnd ?? [], .. png.AsSpan(end), .. afterEnd ?? []];
    }

    /// <summary>The JPEG with segments after its start-of-image marker, segments before its end-of-image marker and bytes after it.</summary>
    public static byte[] InsertJpeg(byte[] jpeg, byte[] afterStart, byte[] beforeEnd = null, byte[] afterEnd = null)
    {
        var end = jpeg.Length - 2;
        return [.. jpeg.AsSpan(0, 2), .. afterStart, .. jpeg.AsSpan(2, end - 2), .. beforeEnd ?? [], .. jpeg.AsSpan(end), .. afterEnd ?? []];
    }

    #endregion

    #region Pieces

    /// <summary>A PNG chunk with its CRC.</summary>
    public static byte[] Chunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    /// <summary>A JPEG segment: FF, its marker, its length (which counts itself) and its payload.</summary>
    public static byte[] Segment(byte marker, byte[] payload)
    {
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment, 4);
        return segment;
    }

    /// <summary>An EXIF payload: the identifier and a little-endian TIFF header with a position in its text.</summary>
    public static byte[] Exif => [.. Latin1("Exif\0\0II*\0"), 8, 0, 0, 0, .. Latin1("GPSLatitude 31.9539N GPSLongitude 35.9106E CadExport")];

    /// <summary>An XMP packet naming its author and tool.</summary>
    public const string Xmp =
        """<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description xmlns:dc="http://purl.org/dc/elements/1.1/" dc:creator="Plan author" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmp:CreatorTool="CadExport 7.2"/></rdf:RDF></x:xmpmeta>""";

    public static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    #endregion

    #region Readers

    /// <summary>Which of <paramref name="texts"/> are in the bytes, in their order (read as Latin-1, so each byte matches itself).</summary>
    public static IReadOnlyList<string> Found(byte[] bytes, params string[] texts)
    {
        var text = Encoding.Latin1.GetString(bytes);
        return [.. texts.Where(t => text.Contains(t, StringComparison.Ordinal))];
    }

    /// <summary>The chunks of a PNG, in order, as type and data; stops at IEND (anything after it is not a chunk).</summary>
    public static IReadOnlyList<(string Type, byte[] Data)> Chunks(byte[] png)
    {
        var chunks = new List<(string, byte[])>();
        var at = 8;
        while (at + 12 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            var type = Encoding.ASCII.GetString(png, at + 4, 4);
            chunks.Add((type, png.AsSpan(at + 8, length).ToArray()));
            at += 12 + length;
            if (type == "IEND")
                break;
        }

        return chunks;
    }

    /// <summary>The markers of a JPEG's segments up to and including its first scan header, in order.</summary>
    public static IReadOnlyList<byte> HeaderMarkers(byte[] jpeg)
    {
        var markers = new List<byte>();
        var at = 2;
        while (at + 4 <= jpeg.Length && jpeg[at] == 0xFF)
        {
            var marker = jpeg[at + 1];
            markers.Add(marker);
            if (marker == 0xDA)
                break;
            at += 2 + ((jpeg[at + 2] << 8) | jpeg[at + 3]);
        }

        return markers;
    }

    /// <summary>The image's pixels as decoded, with its size; null when it does not decode.</summary>
    public static (int Width, int Height, SKColor[] Pixels)? Decode(byte[] bytes)
    {
        using var bitmap = SKBitmap.Decode(bytes);
        return bitmap is null ? null : (bitmap.Width, bitmap.Height, bitmap.Pixels);
    }

    #endregion

    #region Helpers

    private static byte[] Encode(SKEncodedImageFormat format, int width, int height)
    {
        using var bitmap = Picture(width, height);
        using var data = bitmap.Encode(format, 90);
        return data.ToArray();
    }

    private static byte[] Deflated(string text)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(Latin1(text));
        return output.ToArray();
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

    #endregion
}
