using System.Globalization;

namespace Ariva.Core.Domain.Components;

/// <summary>A point in floor coordinates (metres, the level's local system: origin at a corner, x along the width).</summary>
public readonly record struct FloorPoint(double X, double Y)
{
    /// <summary>Coordinates are kept to the millimetre: below that a sensor cannot place a person anyway.</summary>
    public FloorPoint Rounded() => new(Mm(X), Mm(Y));

    // Adding 0.0 turns -0 into 0, so -0.0004 and 0.0004 print alike and hash alike.
    private static double Mm(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero) + 0.0;

    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{X:0.000} {Y:0.000}");
}

/// <summary>
/// Plane geometry for zones and lines (ARV-016). Pure and deterministic; distances in metres. Two points closer than
/// <see cref="Tolerance"/> are the same point, and a point that close to a segment lies on it.
/// </summary>
public static class Geometry
{
    /// <summary>One centimetre: below a sensor's resolution, above floating point noise.</summary>
    public const double Tolerance = 0.01;

    /// <summary>Signed area by the shoelace formula: positive for counter-clockwise rings.</summary>
    public static double SignedArea(IReadOnlyList<FloorPoint> ring)
    {
        ArgumentNullException.ThrowIfNull(ring);
        var sum = 0.0;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return sum / 2;
    }

    public static double Length(FloorPoint a, FloorPoint b) => Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));

    /// <summary>Distance from a point to the closed segment a-b.</summary>
    public static double DistanceToSegment(FloorPoint p, FloorPoint a, FloorPoint b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared == 0)
            return Length(p, a);
        var t = Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lengthSquared, 0, 1);
        return Length(p, new FloorPoint(a.X + (t * dx), a.Y + (t * dy)));
    }

    /// <summary>True when the closed segments p1-p2 and q1-q2 touch or cross (collinear overlaps included).</summary>
    public static bool SegmentsIntersect(FloorPoint p1, FloorPoint p2, FloorPoint q1, FloorPoint q2)
    {
        var d1 = Cross(q1, q2, p1);
        var d2 = Cross(q1, q2, p2);
        var d3 = Cross(p1, p2, q1);
        var d4 = Cross(p1, p2, q2);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            return true;
        return (d1 == 0 && OnSegment(q1, q2, p1)) || (d2 == 0 && OnSegment(q1, q2, p2)) ||
               (d3 == 0 && OnSegment(p1, p2, q1)) || (d4 == 0 && OnSegment(p1, p2, q2));
    }

    /// <summary>
    /// A simple polygon: at least three distinct vertices, no repeated vertex, non-zero area, and no two edges touching
    /// except neighbours at their shared vertex.
    /// </summary>
    public static bool IsSimplePolygon(IReadOnlyList<FloorPoint> ring)
    {
        if (ring is null || ring.Count < 3)
            return false;
        for (var i = 0; i < ring.Count; i++)
        {
            for (var j = i + 1; j < ring.Count; j++)
            {
                if (Length(ring[i], ring[j]) < Tolerance)
                    return false;
            }
        }

        if (Math.Abs(SignedArea(ring)) < Tolerance * Tolerance)
            return false;

        var n = ring.Count;
        for (var i = 0; i < n; i++)
        {
            var a1 = ring[i];
            var a2 = ring[(i + 1) % n];
            for (var j = i + 1; j < n; j++)
            {
                var neighbours = j == i + 1 || (i == 0 && j == n - 1);
                if (neighbours)
                {
                    // Neighbours share one vertex; they must not fold back onto each other.
                    var shared = j == i + 1 ? ring[j] : ring[0];
                    var otherA = j == i + 1 ? a1 : a2;
                    var otherB = j == i + 1 ? ring[(j + 1) % n] : ring[j];
                    if (Cross(otherA, shared, otherB) == 0 && Dot(otherA, shared, otherB) > 0)
                        return false;
                    continue;
                }

                if (SegmentsIntersect(a1, a2, ring[j], ring[(j + 1) % n]))
                    return false;
            }
        }

        return true;
    }

    /// <summary>The index of the polygon edge the segment a-b lies on (both ends within tolerance), or -1.</summary>
    public static int EdgeContaining(IReadOnlyList<FloorPoint> ring, FloorPoint a, FloorPoint b)
    {
        ArgumentNullException.ThrowIfNull(ring);
        for (var i = 0; i < ring.Count; i++)
        {
            var e1 = ring[i];
            var e2 = ring[(i + 1) % ring.Count];
            if (DistanceToSegment(a, e1, e2) <= Tolerance && DistanceToSegment(b, e1, e2) <= Tolerance)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Parses "x y,x y,..." (metres, invariant culture) into points; null when the text is malformed, a coordinate is not
    /// finite, or there are more than <paramref name="maxPoints"/> points.
    /// </summary>
    public static IReadOnlyList<FloorPoint> ParseRing(string text, int maxPoints)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var parts = text.Split(',');
        if (parts.Length > maxPoints)
            return null;
        var points = new List<FloorPoint>(parts.Length);
        foreach (var part in parts)
        {
            var xy = part.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (xy.Length != 2 ||
                !double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                return null;
            }

            var point = new FloorPoint(x, y);
            if (!point.IsFinite)
                return null;
            points.Add(point);
        }

        return points;
    }

    /// <summary>The canonical text of a ring: millimetre precision, invariant culture, "x y,x y".</summary>
    public static string FormatRing(IEnumerable<FloorPoint> ring) => string.Join(",", ring.Select(p => p.Rounded().ToString()));

    private static double Cross(FloorPoint o, FloorPoint a, FloorPoint b)
    {
        var value = ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));
        return Math.Abs(value) < 1e-12 ? 0 : value;
    }

    private static double Dot(FloorPoint a, FloorPoint shared, FloorPoint b) =>
        ((a.X - shared.X) * (b.X - shared.X)) + ((a.Y - shared.Y) * (b.Y - shared.Y));

    private static bool OnSegment(FloorPoint a, FloorPoint b, FloorPoint p) =>
        Math.Min(a.X, b.X) - 1e-12 <= p.X && p.X <= Math.Max(a.X, b.X) + 1e-12 &&
        Math.Min(a.Y, b.Y) - 1e-12 <= p.Y && p.Y <= Math.Max(a.Y, b.Y) + 1e-12;
}
