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
            return FloorPlanLimits.Fits(width, height) ? new InspectedFile("image/png", "png", bytes, width, height) : null;
        }

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            var (width, height) = JpegSize(bytes);
            return width is { } w && height is { } h && FloorPlanLimits.Fits(w, h)
                ? new InspectedFile("image/jpeg", "jpg", bytes, w, h)
                : null;
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

    /// <summary>Width and height from the first start-of-frame marker, or nulls.</summary>
    private static (int? Width, int? Height) JpegSize(byte[] bytes)
    {
        var i = 2;
        while (i + 9 < bytes.Length)
        {
            if (bytes[i] != 0xFF)
                return (null, null);
            var marker = bytes[i + 1];
            var length = (bytes[i + 2] << 8) | bytes[i + 3];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return ((bytes[i + 7] << 8) | bytes[i + 8], (bytes[i + 5] << 8) | bytes[i + 6]);
            if (length < 2)
                return (null, null);
            i += 2 + length;
        }

        return (null, null);
    }

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
