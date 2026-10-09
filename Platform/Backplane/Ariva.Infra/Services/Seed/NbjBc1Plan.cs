using System.Globalization;
using System.Security;
using System.Text;
using static Ariva.Infra.Services.Seed.NbjBc1Layout;

namespace Ariva.Infra.Services.Seed;

/// <summary>
/// The schematic floor plan of each NBJ BC1 border control hall (ARV-139c): an SVG drawn by Ariva from
/// <see cref="NbjBc1Layout"/> at 20 pixels per metre (origin at the top left), labelled "Illustrative, not surveyed".
/// It is Ariva's own schematic of the counts and measures read from the design drawings, never the drawings themselves
/// (a third party's confidential work, kept outside the repository). The seed stores it through the floor plan pipeline
/// (typed by its bytes and sanitised, ARV-018) like any upload; a surveyed plan replaces it at runtime.
/// </summary>
public static class NbjBc1Plan
{
    private const double PixelsPerMetre = 1 / MetresPerPixel;

    public static string FileName(Hall hall)
    {
        ArgumentNullException.ThrowIfNull(hall);
        return $"nbj-bc1-{hall.LevelCode.ToLowerInvariant()}-schematic.svg";
    }

    /// <summary>A hall's plan width and height in pixels.</summary>
    public static (int Width, int Height) Size(Hall hall)
    {
        ArgumentNullException.ThrowIfNull(hall);
        return ((int)(hall.WidthMetres * PixelsPerMetre), (int)(hall.DepthMetres * PixelsPerMetre));
    }

    /// <summary>The SVG of a hall as UTF-8 bytes; the same bytes on every call.</summary>
    public static byte[] Svg(Hall hall)
    {
        var (width, height) = Size(hall);
        var svg = new StringBuilder();
        Append(svg, $"""<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" font-family="sans-serif">""");
        Append(svg, $"<title>Illustrative, not surveyed: NBJ terminal BC1, {SecurityElement.Escape(hall.CheckpointName.ToLowerInvariant())}, schematic from 2018 design drawings</title>");
        Rect(svg, 0, 0, hall.WidthMetres, hall.DepthMetres, "#f7f7f9", "#9aa0a6");

        // Upstream: the health counters (arrivals) or the security lanes' exits (departures).
        Rect(svg, 1, 1.5, hall.WidthMetres - 1, 5, "#eef3f9", "#9aa0a6");
        if (hall.Arrivals)
        {
            for (var n = 0; n < 8; n++)
                Rect(svg, 10 + (n * 5.2), 2.6, 13 + (n * 5.2), 4, "#ffffff", "#7a869a");
            Text(svg, hall.WidthMetres / 2, 1.3, 26, "Health counters (8)", true);
        }
        else
        {
            for (var n = 0; n < 12; n++)
                Rect(svg, 6 + (n * 4.4), 2.4, 7 + (n * 4.4), 5, "#ffffff", "#7a869a");
            Text(svg, hall.WidthMetres / 2, 1.3, 26, "Security lanes (exits)", true);
        }

        // The hall: overflow band and the shared queue, the e-gates' queue.
        var band = hall.OverflowRect();
        Rect(svg, band[0].X, band[0].Y, band[2].X, band[2].Y, "#fdf6e3", "#d4b26a");
        Text(svg, (band[0].X + band[2].X) / 2, (band[0].Y + band[2].Y) / 2 + 0.4, 26, $"{hall.OverflowName} (overflow)", true);
        var queue = hall.QueueRect();
        Rect(svg, queue[0].X, queue[0].Y, queue[2].X, queue[2].Y, "#ffffff", "#c4c8cc");
        Text(svg, (queue[0].X + queue[2].X) / 2, (queue[0].Y + queue[2].Y) / 2, 30, $"{hall.QueueName}: one shared queue, no lane segregation", true);
        var gates = hall.EGateQueueRect();
        Rect(svg, gates[0].X, gates[0].Y, gates[2].X, gates[2].Y, "#e8f0fb", "#5b8def");

        // The row: 13 double booths (26 desks) and 5 e-gate channels.
        for (var booth = 0; booth < BoothsPerRow; booth++)
        {
            var left = hall.FirstBoothX + (booth * hall.BoothPitch);
            Rect(svg, left, hall.BoothStartY, left + hall.BoothWidth, hall.BoothEndY, "#7a869a", "#4a5568");
            Line(svg, left + (hall.BoothWidth / 2), hall.BoothStartY, left + (hall.BoothWidth / 2), hall.BoothEndY);
        }

        for (var n = 1; n <= DesksPerRow; n += 2)
        {
            var (left, _) = hall.DeskBand(n);
            Text(svg, left + (hall.BoothWidth / 2), hall.BoothEndY + 1.1, 16, hall.DeskCode(n)[^2..] + "/" + hall.DeskCode(n + 1)[^2..], true);
        }

        var (gateLeft, gateRight) = hall.EGateBand;
        for (var n = 0; n < EGatesPerRow; n++)
            Rect(svg, gateLeft + (n * EGatePitch) + 0.15, hall.BoothStartY, gateLeft + ((n + 1) * EGatePitch) - 0.15, hall.BoothEndY, "#5b8def", "#2f5fb3");
        Text(svg, (gateLeft + gateRight) / 2, hall.BoothEndY + 2.4, 18, $"E-gates ({EGatesPerRow})", true);

        Text(svg, hall.WidthMetres / 2, hall.BoothEndY + 3.6, 24,
            $"{hall.CheckpointName}: {BoothsPerRow} double booths, desks {hall.DeskCode(1)} to {hall.DeskCode(DesksPerRow)}", true);
        Text(svg, hall.WidthMetres / 2, hall.DepthMetres - 1.2, 22, hall.DownstreamLabel, true);
        Text(svg, hall.WidthMetres / 2, 5.8, 20, hall.UpstreamLabel, true);

        Text(svg, hall.WidthMetres - 1, hall.DepthMetres - 3.2, 22, "Illustrative, not surveyed", false, anchorEnd: true);
        Text(svg, hall.WidthMetres - 1, hall.DepthMetres - 2.2, 16, "Schematic from 2018 design drawings; the as-built hall may differ", false, anchorEnd: true);
        Append(svg, "</svg>");
        return Encoding.UTF8.GetBytes(svg.ToString());
    }

    private static string Px(double metres) => (metres * PixelsPerMetre).ToString("0.#", CultureInfo.InvariantCulture);

    private static void Append(StringBuilder svg, string text) => svg.Append(text).Append('\n');

    private static void Rect(StringBuilder svg, double x0, double y0, double x1, double y1, string fill, string stroke) =>
        Append(svg, $"""<rect x="{Px(x0)}" y="{Px(y0)}" width="{Px(x1 - x0)}" height="{Px(y1 - y0)}" fill="{fill}" stroke="{stroke}" stroke-width="2"/>""");

    private static void Line(StringBuilder svg, double x0, double y0, double x1, double y1) =>
        Append(svg, $"""<line x1="{Px(x0)}" y1="{Px(y0)}" x2="{Px(x1)}" y2="{Px(y1)}" stroke="#e2e8f0" stroke-width="2"/>""");

    private static void Text(StringBuilder svg, double x, double y, int size, string text, bool centred, bool anchorEnd = false)
    {
        var anchor = centred ? " text-anchor=\"middle\"" : anchorEnd ? " text-anchor=\"end\"" : string.Empty;
        Append(svg, $"""<text x="{Px(x)}" y="{Px(y)}" font-size="{size.ToString(CultureInfo.InvariantCulture)}"{anchor} fill="#202124">{SecurityElement.Escape(text)}</text>""");
    }
}
