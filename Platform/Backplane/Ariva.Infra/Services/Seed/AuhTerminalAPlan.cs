using System.Globalization;
using System.Security;
using System.Text;
using static Ariva.Infra.Services.Seed.AuhTerminalALayout;

namespace Ariva.Infra.Services.Seed;

/// <summary>
/// The illustrative floor plan of the AUH Terminal A arrivals level (ARV-139a): a schematic SVG drawn from
/// <see cref="AuhTerminalALayout"/> at 10 pixels per metre (origin at the top left), labelled "Illustrative, not surveyed".
/// It is not a survey and not the airport's plan: the hall's arrangement and every position are assumptions; only the
/// counter and gate counts (reported) and the straight line from immigration to baggage claim, customs and landside
/// transport come from public information. The seed stores it through the floor plan pipeline (typed by its bytes and
/// sanitised, ARV-018) like any upload; a real plan replaces it at runtime through the floor plan screen and is never
/// committed (docs/demo/auh-terminal-a.md).
/// </summary>
public static class AuhTerminalAPlan
{
    public const string FileName = "auh-terminal-a-arrivals-illustrative.svg";

    private const double PixelsPerMetre = 1 / MetresPerPixel;

    /// <summary>The plan's width and height in pixels.</summary>
    public static (int Width, int Height) Size => ((int)(WidthMetres * PixelsPerMetre), (int)(DepthMetres * PixelsPerMetre));

    /// <summary>The SVG as UTF-8 bytes; the same bytes on every call.</summary>
    public static byte[] Svg()
    {
        var (width, height) = Size;
        var svg = new StringBuilder();
        Append(svg, $"""<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" font-family="sans-serif">""");
        Append(svg, "<title>Illustrative, not surveyed: AUH Terminal A arrivals, schematic from public information</title>");
        Rect(svg, 0, 0, WidthMetres, DepthMetres, "#f7f7f9", "#9aa0a6");

        // The flow, in a straight line (public): airside, immigration, baggage claim, customs, landside transport.
        Rect(svg, 0, 0, 50, DepthMetres, "#e8f0fb", "#9aa0a6");
        Text(svg, 25, 70, 28, "From the aircraft stands", true);
        Text(svg, 25, 74, 28, "(airside corridor)", true);
        Rect(svg, 56, 2, QueueEndX, DepthMetres - 2, "#ffffff", "#c4c8cc");
        Text(svg, 84, 51, 24, "Immigration queue hall", true);
        Rect(svg, 135, 10, 172, 140, "#eef7ee", "#9aa0a6");
        Text(svg, 153.5, 20, 28, "Baggage claim", true);
        for (var i = 0; i < 4; i++)
            Rect(svg, 145, 30 + (i * 27), 162, 48 + (i * 27), "#ffffff", "#7d8b7d", rounded: true);
        Rect(svg, 176, 10, 186, 140, "#fbf3e6", "#9aa0a6");
        Text(svg, 181, 76, 24, "Customs", true);
        Rect(svg, 189, 0, WidthMetres, DepthMetres, "#f1ecf7", "#9aa0a6");
        Text(svg, 194.5, 72, 22, "Landside", true);
        Text(svg, 194.5, 76, 22, "transport", true);

        // Smart gates and counters (reported counts), drawn as booths in one line.
        for (var n = 1; n <= SmartGates; n++)
        {
            var top = GateStartY + ((n - 1) * GatePitch);
            Rect(svg, ServiceEndX, top + 0.2, BoothEndX, top + GatePitch - 0.2, "#5b8def", "#2f5fb3");
        }

        Text(svg, 124, (GateBand.Top + GateBand.Bottom) / 2, 20, $"Smart gates {SmartGateCode(1)} to {SmartGateCode(SmartGates)} ({SmartGates}, reported)", false);
        for (var n = 1; n <= Counters; n++)
        {
            var (top, bottom) = CounterBand(n);
            Rect(svg, ServiceEndX, top + 0.2, BoothEndX, bottom - 0.2, "#7a869a", "#4a5568");
            Text(svg, 124, bottom - 0.6, 14, $"{CounterCode(n)} {LaneOfCounter(n).Code}", false);
        }

        Text(svg, 60, 148, 22, $"Immigration counters {CounterCode(1)} to {CounterCode(Counters)} ({Counters}, reported); lanes by counter are assumptions", false);
        Append(svg, $"""<path d="M {Px(4)} {Px(146)} L {Px(46)} {Px(146)} M {Px(42)} {Px(144.5)} L {Px(46)} {Px(146)} L {Px(42)} {Px(147.5)}" fill="none" stroke="#2f5fb3" stroke-width="4"/>""");
        Text(svg, 25, 143, 20, "Passenger flow", true);

        Text(svg, 160, 4, 26, "Illustrative, not surveyed", true);
        Text(svg, 160, 7.5, 18, "Schematic from public information; positions are assumptions", true);
        Append(svg, "</svg>");
        return Encoding.UTF8.GetBytes(svg.ToString());
    }

    private static string Px(double metres) => (metres * PixelsPerMetre).ToString("0.#", CultureInfo.InvariantCulture);

    private static void Append(StringBuilder svg, string text) => svg.Append(text).Append('\n');

    private static void Rect(StringBuilder svg, double x0, double y0, double x1, double y1, string fill, string stroke, bool rounded = false) =>
        Append(svg, $"""<rect x="{Px(x0)}" y="{Px(y0)}" width="{Px(x1 - x0)}" height="{Px(y1 - y0)}"{(rounded ? " rx=\"20\"" : string.Empty)} fill="{fill}" stroke="{stroke}" stroke-width="2"/>""");

    private static void Text(StringBuilder svg, double x, double y, int size, string text, bool centred) =>
        Append(svg, $"""<text x="{Px(x)}" y="{Px(y)}" font-size="{size.ToString(CultureInfo.InvariantCulture)}"{(centred ? " text-anchor=\"middle\"" : string.Empty)} fill="#202124">{SecurityElement.Escape(text)}</text>""");
}
