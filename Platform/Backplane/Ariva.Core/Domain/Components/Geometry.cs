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

    /// <summary>
    /// True when the closed segments p1-p2 and q1-q2 touch or cross (collinear overlaps included). An end of one segment
    /// closer than <see cref="Tolerance"/> to the other segment touches it, so an exact touch is a touch whatever the
    /// rounding. The answer is the same for every order of the two segments and direction of either (ARV-161). Zone
    /// validity (<see cref="IsSimplePolygon"/>) and a device footprint's reach (<see cref="Overlaps"/>) use it; counting
    /// line crossings from tracks does not (F4's own side-of-line rule, docs/domain/formulas.md).
    /// </summary>
    public static bool SegmentsIntersect(FloorPoint p1, FloorPoint p2, FloorPoint q1, FloorPoint q2)
    {
        // Order independence by construction (ARV-161): each segment's ends are put in a fixed order, then the two segments,
        // so all eight orders and directions of the arguments reach the arithmetic below as the same four points, bit for
        // bit, and round alike. The answer is then a function of the unordered pair of unordered segments.
        var (a1, a2) = Ordered(p1, p2);
        var (b1, b2) = Ordered(q1, q2);
        var first = Compare(a1, b1);
        if (first > 0 || (first == 0 && Compare(a2, b2) > 0))
            (a1, a2, b1, b2) = (b1, b2, a1, a2);

        // A touch: an end closer than Tolerance to the other segment (a shared end, an end on the other segment, a
        // collinear overlap, a segment of no length lying on the other). A distance in metres, compared with a length.
        if (DistanceToSegment(a1, b1, b2) < Tolerance || DistanceToSegment(a2, b1, b2) < Tolerance ||
            DistanceToSegment(b1, a1, a2) < Tolerance || DistanceToSegment(b2, a1, a2) < Tolerance)
        {
            return true;
        }

        // Otherwise only a proper crossing: each segment's ends on opposite sides of the other's line, each at least
        // Tolerance from it, where the sign of a cross product is never in doubt. An end closer than that to the other's
        // line counts as on it (Side 0); with no touch above, such segments cannot cross, because the part of the segment
        // between that end and a crossing would pass within Tolerance of an end of the other segment.
        return Side(b1, b2, a1) * Side(b1, b2, a2) < 0 && Side(a1, a2, b1) * Side(a1, a2, b2) < 0;
    }

    /// <summary>
    /// True when <paramref name="p"/> lies inside the ring or on its boundary (within <see cref="Tolerance"/>), by
    /// ray casting. The ring is closed implicitly (last vertex to first).
    /// </summary>
    public static bool ContainsPoint(IReadOnlyList<FloorPoint> ring, FloorPoint p)
    {
        if (ring is null || ring.Count < 3)
            return false;
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[j];
            var b = ring[i];
            if (DistanceToSegment(p, a, b) <= Tolerance)
                return true;
            if ((b.Y > p.Y) != (a.Y > p.Y) && p.X < ((a.X - b.X) * (p.Y - b.Y) / (a.Y - b.Y)) + b.X)
                inside = !inside;
        }

        return inside;
    }

    /// <summary>
    /// True when two rings share any area or boundary point: an edge of one touches an edge of the other, or one lies
    /// inside the other. Used to check that a sensor's coverage footprint reaches its zone (ARV-021). Edges touch as
    /// <see cref="SegmentsIntersect"/> says (within <see cref="Tolerance"/>), so rings that only touch overlap, in any vertex
    /// order (ARV-161).
    /// </summary>
    public static bool Overlaps(IReadOnlyList<FloorPoint> first, IReadOnlyList<FloorPoint> second)
    {
        if (first is null || second is null || first.Count < 3 || second.Count < 3)
            return false;
        for (int i = 0, j = first.Count - 1; i < first.Count; j = i++)
        {
            for (int k = 0, l = second.Count - 1; k < second.Count; l = k++)
            {
                if (SegmentsIntersect(first[j], first[i], second[l], second[k]))
                    return true;
            }
        }

        return ContainsPoint(second, first[0]) || ContainsPoint(first, second[0]);
    }

    /// <summary>
    /// A simple polygon: at least three distinct vertices, no repeated vertex, non-zero area, and no two edges touching
    /// except neighbours at their shared vertex. Edges that are not neighbours touch as <see cref="SegmentsIntersect"/>
    /// says, so a vertex on, or closer than <see cref="Tolerance"/> to, an edge that is not its own is refused, in any vertex
    /// order (ARV-161).
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

    /// <summary>
    /// The side of the line through a and b that r lies on: 1 to the left, -1 to the right, 0 when r is closer than
    /// <see cref="Tolerance"/> to the line (or a and b are one point). The cross product is a length squared (twice the
    /// triangle's area), so it is compared with Tolerance times the length of a-b: the point's distance to the line
    /// against a length. A NaN cross product gives 0.
    /// </summary>
    private static int Side(FloorPoint a, FloorPoint b, FloorPoint r)
    {
        var cross = ((b.X - a.X) * (r.Y - a.Y)) - ((b.Y - a.Y) * (r.X - a.X));
        if (Math.Abs(cross) < Tolerance * Length(a, b))
            return 0;
        return cross > 0 ? 1 : cross < 0 ? -1 : 0;
    }

    /// <summary>The ends of a segment in a fixed order (by X, then Y); negative zero is made zero so equal ends are equal bits.</summary>
    private static (FloorPoint, FloorPoint) Ordered(FloorPoint a, FloorPoint b)
    {
        a = new FloorPoint(a.X + 0.0, a.Y + 0.0);
        b = new FloorPoint(b.X + 0.0, b.Y + 0.0);
        return Compare(a, b) <= 0 ? (a, b) : (b, a);
    }

    /// <summary>A total order on points: X, then Y (<see cref="double.CompareTo(double)"/>, which also orders NaN).</summary>
    private static int Compare(FloorPoint a, FloorPoint b)
    {
        var byX = a.X.CompareTo(b.X);
        return byX != 0 ? byX : a.Y.CompareTo(b.Y);
    }
}
