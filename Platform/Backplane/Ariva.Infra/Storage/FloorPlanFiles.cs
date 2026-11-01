using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Ariva.Infra.Storage;

/// <summary>What an uploaded plan turned out to be: its real type, the bytes to store, and the image size when known.</summary>
public sealed record InspectedFile(string ContentType, string Extension, byte[] Bytes, int? WidthPixels, int? HeightPixels);

/// <summary>
/// Inspects floor plan uploads (ARV-018, CWE-79, CWE-434). The type comes from the bytes, never from the name or the
/// declared content type: PNG and JPEG by their signatures and header (with a size cap, so no viewer has to decode a
/// pixel bomb), SVG by parsing it as XML whose root is an svg element. SVG is streamed through an allowlist and
/// written anew: only listed SVG drawing elements and listed attributes survive; references must point inside the
/// document (<c>#id</c>); CSS escapes, comments, processing instructions and everything in another namespace are
/// dropped; CDATA becomes escaped text; DTDs are refused (CWE-611); element count and depth are capped (CWE-400).
/// PNG and JPEG are copied piece by piece through an allowlist (ARV-201), never decoded: the pixels, their size and
/// what draws them stay as uploaded, while metadata (EXIF, XMP, text chunks, comments, colour profiles, thumbnails) and
/// anything after the image's end are dropped, and a file whose structure does not read through to its end is refused.
/// </summary>
public static partial class FloorPlanFiles
{
    private const string SvgNamespace = "http://www.w3.org/2000/svg";
    private const string XLinkNamespace = "http://www.w3.org/1999/xlink";
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";

    /// <summary>Drawing elements kept. Everything else (script, style, foreignObject, image, a, animation, filters,
    /// metadata, and any element outside the SVG namespace) is dropped with its content.</summary>
    private static readonly HashSet<string> Elements = new(StringComparer.Ordinal)
    {
        "svg", "g", "defs", "title", "desc", "symbol", "use", "path", "rect", "circle", "ellipse", "line", "polyline",
        "polygon", "text", "tspan", "textPath", "linearGradient", "radialGradient", "stop", "pattern", "clipPath", "mask",
        "marker"
    };

    /// <summary>Geometry and structure attributes kept as they are (after the value check).</summary>
    private static readonly HashSet<string> Attributes = new(StringComparer.Ordinal)
    {
        "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry", "fx", "fy", "width", "height", "d", "points",
        "transform", "viewBox", "preserveAspectRatio", "version", "dx", "dy", "rotate", "textLength", "lengthAdjust",
        "startOffset", "offset", "gradientUnits", "gradientTransform", "spreadMethod", "patternUnits",
        "patternContentUnits", "patternTransform", "clipPathUnits", "maskUnits", "maskContentUnits", "markerWidth",
        "markerHeight", "markerUnits", "refX", "refY", "orient", "pathLength"
    };

    /// <summary>Presentation properties, kept as attributes and inside style attributes.</summary>
    private static readonly HashSet<string> Presentation = new(StringComparer.OrdinalIgnoreCase)
    {
        "fill", "fill-opacity", "fill-rule", "stroke", "stroke-width", "stroke-opacity", "stroke-linecap",
        "stroke-linejoin", "stroke-dasharray", "stroke-dashoffset", "stroke-miterlimit", "opacity", "color",
        "font-family", "font-size", "font-weight", "font-style", "font-variant", "font-stretch", "text-anchor",
        "dominant-baseline", "alignment-baseline", "baseline-shift", "text-decoration", "letter-spacing", "word-spacing",
        "writing-mode", "stop-color", "stop-opacity", "clip-path", "clip-rule", "mask", "marker-start", "marker-mid",
        "marker-end", "visibility", "display", "vector-effect", "shape-rendering", "text-rendering", "paint-order"
    };

    /// <summary>The upload as a stored file, or null when it is not a PNG, JPEG or SVG within the limits.</summary>
    public static InspectedFile Inspect(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            // IHDR must be the first chunk: its type at offset 12, width and height big-endian at 16 and 20.
            if (!bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
                return null;
            var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
            if (!FloorPlanLimits.Fits(width, height))
                return null;
            var png = StripPng(bytes);
            return png is null ? null : new InspectedFile("image/png", "png", png, width, height);
        }

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            var jpeg = StripJpeg(bytes, out var width, out var height);
            return jpeg is not null && FloorPlanLimits.Fits(width, height) ? new InspectedFile("image/jpeg", "jpg", jpeg, width, height) : null;
        }

        var svg = SanitizeSvg(bytes, out var svgWidth, out var svgHeight);
        return svg is null ? null : new InspectedFile("image/svg+xml", "svg", svg, svgWidth, svgHeight);
    }

    /// <summary>The SVG rebuilt without active content, or null when the bytes are not an SVG document within the limits.</summary>
    public static byte[] SanitizeSvg(byte[] bytes) => SanitizeSvg(bytes, out _, out _);

    private static byte[] SanitizeSvg(byte[] bytes, out int? width, out int? height)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        width = null;
        height = null;
        var readerSettings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true,
            MaxCharactersInDocument = FloorPlanLimits.MaxSvgCharacters
        };
        var writerSettings = new XmlWriterSettings { OmitXmlDeclaration = true, Encoding = new UTF8Encoding(false), CloseOutput = false };

        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(input, readerSettings);
            using var output = new MemoryStream(bytes.Length);
            var elements = 0;
            var rootSeen = false;
            using (var writer = XmlWriter.Create(output, writerSettings))
            {
                while (reader.Read())
                {
                    switch (reader.NodeType)
                    {
                        case XmlNodeType.Element:
                            if (!rootSeen)
                            {
                                if (reader.LocalName != "svg" || reader.NamespaceURI != SvgNamespace)
                                    return null;
                                rootSeen = true;
                                (width, height) = SvgSize(reader);
                            }

                            if (++elements > FloorPlanLimits.MaxSvgElements || reader.Depth >= FloorPlanLimits.MaxSvgDepth)
                                return null;
                            if (reader.NamespaceURI != SvgNamespace || !Elements.Contains(reader.LocalName))
                            {
                                if (!reader.IsEmptyElement)
                                    SkipSubtree(reader, ref elements);
                                continue;
                            }

                            var empty = reader.IsEmptyElement;
                            writer.WriteStartElement(null, reader.LocalName, SvgNamespace);
                            WriteAttributes(reader, writer);
                            if (empty)
                                writer.WriteEndElement();
                            break;
                        case XmlNodeType.EndElement:
                            writer.WriteFullEndElement();
                            break;
                        case XmlNodeType.Text:
                        case XmlNodeType.CDATA:
                            writer.WriteString(reader.Value);
                            break;
                        case XmlNodeType.Whitespace:
                        case XmlNodeType.SignificantWhitespace:
                            if (rootSeen)
                                writer.WriteWhitespace(reader.Value);
                            break;
                    }
                }
            }

            return rootSeen ? output.ToArray() : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>Reads past the subtree of the current element, counting what it skips against the element cap.</summary>
    private static void SkipSubtree(XmlReader reader, ref int elements)
    {
        var depth = reader.Depth;
        while (reader.Read() && reader.Depth > depth)
        {
            if (reader.NodeType == XmlNodeType.Element && (++elements > FloorPlanLimits.MaxSvgElements || reader.Depth >= FloorPlanLimits.MaxSvgDepth))
                throw new XmlException("SVG over the element or depth limit.");
        }
    }

    private static void WriteAttributes(XmlReader reader, XmlWriter writer)
    {
        if (!reader.MoveToFirstAttribute())
            return;
        do
        {
            var name = reader.LocalName;
            var ns = reader.NamespaceURI;
            var value = reader.Value;
            if ((ns == XLinkNamespace || ns.Length == 0) && name == "href")
            {
                // References stay inside the document; written as plain href (SVG 2), which every current browser reads.
                if (LocalReference().IsMatch(value))
                    writer.WriteAttributeString("href", value);
            }
            else if (ns == XmlNamespace && name == "space")
            {
                writer.WriteAttributeString("xml", "space", XmlNamespace, value == "preserve" ? "preserve" : "default");
            }
            else if (ns.Length == 0 && name == "id")
            {
                if (LocalReference().IsMatch("#" + value))
                    writer.WriteAttributeString("id", value);
            }
            else if (ns.Length == 0 && name == "style")
            {
                var style = CleanStyle(value);
                if (style.Length > 0)
                    writer.WriteAttributeString("style", style);
            }
            else if (ns.Length == 0 && (Attributes.Contains(name) || Presentation.Contains(name)) && IsSafeValue(value))
            {
                writer.WriteAttributeString(name, value);
            }
        }
        while (reader.MoveToNextAttribute());

        reader.MoveToElement();
    }

    /// <summary>The declarations of a style attribute whose property is listed and whose value is safe.</summary>
    private static string CleanStyle(string style)
    {
        var kept = new List<string>();
        foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = declaration.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
                continue;
            var property = declaration[..colon].Trim();
            var value = declaration[(colon + 1)..].Trim();
            if (Presentation.Contains(property) && IsSafeValue(value))
                kept.Add($"{property}:{value}");
        }

        return string.Join(';', kept);
    }

    /// <summary>
    /// No CSS escapes (which could spell anything), no at-rules, no markup, no script or expression, no image functions,
    /// and every url() a local reference.
    /// </summary>
    private static bool IsSafeValue(string value)
    {
        if (value.Contains('\\') || value.Contains('@') || value.Contains('<') ||
            value.Contains("expression", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("script", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("image", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("element(", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("fade(", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return UrlFunction().Count(value) == LocalUrl().Count(value);
    }

    /// <summary>Pixel size from the viewBox, or from plain width and height (no unit or px), when they fit the limits.</summary>
    private static (int? Width, int? Height) SvgSize(XmlReader reader)
    {
        double? w = null, h = null;
        var viewBox = reader.GetAttribute("viewBox");
        if (viewBox is not null)
        {
            var parts = viewBox.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 && Number(parts[2]) is { } vw && Number(parts[3]) is { } vh)
                (w, h) = (vw, vh);
        }

        w ??= Number(reader.GetAttribute("width")?.Replace("px", "", StringComparison.Ordinal));
        h ??= Number(reader.GetAttribute("height")?.Replace("px", "", StringComparison.Ordinal));
        if (w is not { } width || h is not { } height)
            return (null, null);
        var (iw, ih) = ((int)Math.Round(width), (int)Math.Round(height));
        return FloorPlanLimits.Fits(iw, ih) ? (iw, ih) : (null, null);
    }

    private static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value > 0 && value < int.MaxValue
            ? value
            : null;

    #region Rasters (ARV-201)

    /// <summary>
    /// PNG chunks kept: the four critical ones and the ancillary ones about how the samples read (palette transparency,
    /// gamma, chromaticities, sRGB intent, significant bits), which hold numbers and no text and are kept only at their own
    /// size (<see cref="PngChunkFits"/>). Every other ancillary chunk is dropped: text (<c>tEXt</c>, <c>zTXt</c>,
    /// <c>iTXt</c>, where XMP lives), <c>eXIf</c>, <c>tIME</c>, <c>iCCP</c> (a profile carries its own name and text),
    /// <c>pHYs</c>, <c>bKGD</c>, <c>sPLT</c>, <c>hIST</c>, <c>cICP</c>, the animation chunks, content credentials and private
    /// chunks. An APNG keeps its default image (IDAT) and loses its frames.
    /// </summary>
    private static readonly uint[] PngChunksKept =
    [
        Tag("IHDR"u8), Tag("PLTE"u8), Tag("tRNS"u8), Tag("gAMA"u8), Tag("cHRM"u8), Tag("sRGB"u8), Tag("sBIT"u8), Tag("IDAT"u8),
        Tag("IEND"u8)
    ];

    private static readonly uint Ihdr = Tag("IHDR"u8);
    private static readonly uint Plte = Tag("PLTE"u8);
    private static readonly uint Trns = Tag("tRNS"u8);
    private static readonly uint Gama = Tag("gAMA"u8);
    private static readonly uint Chrm = Tag("cHRM"u8);
    private static readonly uint Srgb = Tag("sRGB"u8);
    private static readonly uint Sbit = Tag("sBIT"u8);
    private static readonly uint Idat = Tag("IDAT"u8);
    private static readonly uint Iend = Tag("IEND"u8);

    private static uint Tag(ReadOnlySpan<byte> type) => BinaryPrimitives.ReadUInt32BigEndian(type);

    /// <summary>
    /// The PNG rewritten from its signature and the chunks in <see cref="PngChunksKept"/>, each copied whole (length,
    /// type, data and CRC), up to IEND; anything after IEND is dropped, and so is a kept ancillary chunk of another size
    /// than its own (as a decoder ignores it). Null when a chunk type is not four ASCII letters, a length runs past the end,
    /// IHDR is not the first chunk (of 13 bytes) or comes twice, a critical chunk is one Ariva does not know (its first
    /// letter upper case: a decoder must refuse it, so it is refused here), PLTE is not one to 256 entries of three bytes,
    /// IEND holds data, or there is no IDAT or no IEND.
    /// </summary>
    private static byte[] StripPng(byte[] bytes)
    {
        using var output = new MemoryStream(bytes.Length);
        output.Write(bytes, 0, 8);
        var at = 8;
        var pixels = false;
        while (bytes.Length - at >= 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at, 4));
            var type = bytes.AsSpan(at + 4, 4);
            if (length > (uint)(bytes.Length - at - 12) || !IsChunkType(type))
                return null;
            var tag = Tag(type);
            if ((at == 8) != (tag == Ihdr) || (tag == Ihdr && length != 13))
                return null;
            if ((tag == Plte && (length is 0 or > 768 || length % 3 != 0)) || (tag == Iend && length != 0))
                return null;
            var size = 12 + (int)length;
            if (Array.IndexOf(PngChunksKept, tag) >= 0)
            {
                if (PngChunkFits(tag, length))
                    output.Write(bytes, at, size);
            }
            else if (char.IsAsciiLetterUpper((char)type[0]))
            {
                return null;
            }

            pixels |= tag == Idat;
            if (tag == Iend)
                return pixels ? output.ToArray() : null;
            at += size;
        }

        return null;
    }

    /// <summary>Whether a kept ancillary chunk has its own size (PNG 11.3): tRNS at most 256 bytes, gAMA 4, cHRM 32, sRGB 1, sBIT 1 to 4.</summary>
    private static bool PngChunkFits(uint tag, uint length) =>
        tag == Trns ? length <= 256
        : tag == Gama ? length == 4
        : tag == Chrm ? length == 32
        : tag == Srgb ? length == 1
        : tag != Sbit || length is >= 1 and <= 4;

    private static bool IsChunkType(ReadOnlySpan<byte> type) =>
        char.IsAsciiLetter((char)type[0]) && char.IsAsciiLetter((char)type[1]) &&
        char.IsAsciiLetter((char)type[2]) && char.IsAsciiLetter((char)type[3]);

    /// <summary>
    /// The JPEG rewritten from its start-of-image marker and the segments that draw it, each copied whole and each of the
    /// length its contents give: the frame header (one only: baseline, extended, progressive or lossless, with Huffman or
    /// arithmetic coding), quantisation (DQT), Huffman (DHT) and arithmetic (DAC) tables, restart interval (DRI), number of
    /// lines (DNL), and each scan header (SOS) with its entropy-coded data as uploaded (stuffed bytes and restart markers
    /// included), then the end-of-image marker; anything after it is dropped.
    /// Two segments are rewritten or kept for how the pixels read: a JFIF APP0 ahead of every segment kept, written again
    /// without its thumbnail, so a decoder still reads the colours as YCbCr, and an Adobe APP14 of its fixed size, whose
    /// transform flag says how the colours are coded. Every other APPn (EXIF, XMP and ICC profiles live in APP1 and APP2)
    /// and every comment (COM) is dropped. Null when the bytes do not read as markers and segments through to an
    /// end-of-image after at least one scan: a segment past the end or of another length than its contents, a scan before
    /// the frame, a second frame, a stray restart, start-of-image or reserved marker, or a hierarchical or extension marker
    /// (DHP, EXP, a differential frame, JPGn), which no browser draws.
    /// </summary>
    /// <param name="bytes">The upload, starting FF D8 FF.</param>
    /// <param name="width">The frame's width in pixels, 0 when there is no frame.</param>
    /// <param name="height">The frame's height in pixels, 0 when there is no frame (or the height is left to DNL).</param>
    private static byte[] StripJpeg(byte[] bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        using var output = new MemoryStream(bytes.Length);
        output.Write([0xFF, 0xD8]);
        var at = 2;
        var frame = false;
        var scanned = false;
        while (true)
        {
            // A marker: FF, any fill bytes (more FF), then its code.
            if (at >= bytes.Length || bytes[at] != 0xFF)
                return null;
            while (at < bytes.Length && bytes[at] == 0xFF)
                at++;
            if (at >= bytes.Length)
                return null;
            var marker = bytes[at++];
            if (marker == 0xD9)
            {
                if (!scanned)
                    return null;
                output.Write([0xFF, 0xD9]);
                return output.ToArray();
            }

            // Every other marker here starts a segment whose length counts itself and its payload.
            if (bytes.Length - at < 2)
                return null;
            var length = (bytes[at] << 8) | bytes[at + 1];
            if (length < 2 || length > bytes.Length - at)
                return null;
            var start = at - 2;
            var end = at + length;
            switch (marker)
            {
                case 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC9 or 0xCA or 0xCB:
                    // Precision, height, width and the component count, then three bytes a component (T.81 B.2.2).
                    if (frame || length < 8 || length != 8 + (3 * bytes[at + 7]))
                        return null;
                    frame = true;
                    height = (bytes[at + 3] << 8) | bytes[at + 4];
                    width = (bytes[at + 5] << 8) | bytes[at + 6];
                    output.Write(bytes, start, end - start);
                    break;
                case 0xC4 or 0xCC or 0xDB:
                    if (!IsWholeTables(marker, bytes.AsSpan(at + 2, length - 2)))
                        return null;
                    output.Write(bytes, start, end - start);
                    break;
                case 0xDC or 0xDD:
                    // A number of lines or a restart interval: two bytes.
                    if (length != 4)
                        return null;
                    output.Write(bytes, start, end - start);
                    break;
                case 0xDA:
                    // The component count, two bytes a component, then three bytes of spectral selection (T.81 B.2.3).
                    if (!frame || length < 3 || length != 6 + (2 * bytes[at + 2]))
                        return null;
                    var data = EntropyCodedEnd(bytes, end);
                    if (data < 0)
                        return null;
                    output.Write(bytes, start, data - start);
                    scanned = true;
                    end = data;
                    break;
                case 0xE0:
                    if (output.Length == 2 && length >= 16 && bytes.AsSpan(at + 2, 5).SequenceEqual("JFIF\0"u8))
                    {
                        // Identifier, version, units and densities as they are; no thumbnail (its width and height 0).
                        output.Write([0xFF, 0xE0, 0x00, 0x10]);
                        output.Write(bytes, at + 2, 12);
                        output.Write([0x00, 0x00]);
                    }

                    break;
                case 0xEE:
                    if (length == 14 && bytes.AsSpan(at + 2, 5).SequenceEqual("Adobe"u8))
                        output.Write(bytes, start, end - start);
                    break;
                case (>= 0xE1 and <= 0xEF) or 0xFE:
                    break;
                default:
                    return null;
            }

            at = end;
        }
    }

    /// <summary>
    /// Whether a table segment's payload is whole tables and nothing else (T.81 B.2.4): DQT 65 bytes a table of 8-bit values
    /// or 129 of 16-bit ones, DHT 17 bytes and its codes (at most 256) a table, DAC 2 bytes a table.
    /// </summary>
    private static bool IsWholeTables(byte marker, ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
            return false;
        while (!payload.IsEmpty)
        {
            int size;
            switch (marker)
            {
                case 0xDB:
                    var precision = payload[0] >> 4;
                    if (precision > 1)
                        return false;
                    size = precision == 0 ? 65 : 129;
                    break;
                case 0xC4:
                    if (payload.Length < 17)
                        return false;
                    var codes = 0;
                    foreach (var count in payload.Slice(1, 16))
                        codes += count;
                    if (codes > 256)
                        return false;
                    size = 17 + codes;
                    break;
                default:
                    size = 2;
                    break;
            }

            if (size > payload.Length)
                return false;
            payload = payload[size..];
        }

        return true;
    }

    /// <summary>
    /// Where a scan's entropy-coded data ends: at the first marker that is neither a stuffed byte (FF 00) nor a restart
    /// (FF D0 to FF D7), fill bytes before it excluded. -1 when the data runs to the end of the file.
    /// </summary>
    private static int EntropyCodedEnd(byte[] bytes, int at)
    {
        while (true)
        {
            var found = bytes.AsSpan(at).IndexOf((byte)0xFF);
            if (found < 0)
                return -1;
            var marker = at + found;
            var code = marker + 1;
            while (code < bytes.Length && bytes[code] == 0xFF)
                code++;
            if (code >= bytes.Length)
                return -1;
            if (bytes[code] is not 0x00 and not (>= 0xD0 and <= 0xD7))
                return marker;
            at = code + 1;
        }
    }

    #endregion

    [GeneratedRegex(@"^#[A-Za-z_][A-Za-z0-9_.:-]*$")]
    private static partial Regex LocalReference();

    [GeneratedRegex(@"url\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex UrlFunction();

    [GeneratedRegex(@"url\s*\(\s*(['""]?)#[A-Za-z_][A-Za-z0-9_.:-]*\1\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex LocalUrl();
}

/// <summary>Limits on plan files.</summary>
public static class FloorPlanLimits
{
    /// <summary>An SVG of 20 MB parses to at most this many characters.</summary>
    public const long MaxSvgCharacters = 25_000_000;

    /// <summary>A CAD export of a large hall stays far below this; a flood of empty elements does not.</summary>
    public const int MaxSvgElements = 200_000;

    public const int MaxSvgDepth = 64;

    /// <summary>Browsers and canvases cope with this much per side.</summary>
    public const int MaxSide = 16_384;

    /// <summary>128 megapixels: about 512 MB once a viewer decodes it. An A0 sheet at 250 dpi is about 97.</summary>
    public const long MaxPixels = 134_217_728;

    public static bool Fits(int width, int height) =>
        width is > 0 and <= MaxSide && height is > 0 and <= MaxSide && (long)width * height <= MaxPixels;
}
