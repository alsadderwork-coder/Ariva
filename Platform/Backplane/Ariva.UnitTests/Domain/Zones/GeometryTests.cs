using Ariva.Core.Domain.Components;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Zones;

/// <summary>ARV-016: the plane geometry behind zone validation, table-driven.</summary>
public sealed class GeometryTests
{
    private static IReadOnlyList<FloorPoint> Ring(string text) => Geometry.ParseRing(text, 200);

    [Theory]
    [InlineData("0 0,10 0,10 10,0 10", true, "square")]
    [InlineData("0 0,10 0,5 8", true, "triangle")]
    [InlineData("0 0,10 0,10 10,5 3,0 10", true, "concave arrow")]
    [InlineData("0 0,10 10,10 0,0 10", false, "bow tie: two edges cross")]
    [InlineData("0 0,10 0,20 0", false, "collinear: no area")]
    [InlineData("0 0,10 0", false, "two points")]
    [InlineData("0 0,10 0,10 10,10 0,0 10", false, "repeated vertex")]
    [InlineData("0 0,10 0,10 10,10 5,10 15,0 10", false, "edge folds back over itself")]
    [InlineData("0 0,10 0,10 10,0 10,5 0", false, "vertex touching another edge")]
    [InlineData("0 0,4 0,4 4,8 4,8 0,12 0,12 8,0 8", true, "U shape")]
    [InlineData("0 0,10 0,10 10,0 10,0 0.005", false, "vertices closer than a centimetre")]
    public void IsSimplePolygon_Should_DecideByShape_When_Checked(string ring, bool simple, string because)
    {
        Geometry.IsSimplePolygon(Ring(ring)).Should().Be(simple, because);
    }

    [Theory]
    [InlineData(double.NaN, double.NaN)]
    [InlineData(double.NaN, 5)]
    [InlineData(5, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, 5)]
    public void IsSimplePolygon_Should_Refuse_When_AVertexIsNotFinite(double x, double y)
    {
        // ParseRing refuses such a vertex already; the predicate refuses it too (every comparison with NaN is false).
        Geometry.IsSimplePolygon([new FloorPoint(0, 0), new FloorPoint(10, 0), new FloorPoint(x, y)]).Should().BeFalse();
        Geometry.IsSimplePolygon([new FloorPoint(0, 0), new FloorPoint(10, 0), new FloorPoint(10, 10), new FloorPoint(x, y)]).Should().BeFalse();
    }

    [Theory]
    [InlineData("0 0,10 0,10 10,0 10", 100)]
    [InlineData("0 0,0 10,10 10,10 0", -100)]
    [InlineData("0 0,4 0,4 4,8 4,8 0,12 0,12 8,0 8", 80)]
    public void SignedArea_Should_FollowTheWinding_When_Computed(string ring, double area)
    {
        Geometry.SignedArea(Ring(ring)).Should().BeApproximately(area, 1e-9);
    }

    [Theory]
    [InlineData(2, 0, 6, 0, 0)]
    [InlineData(10, 2, 10, 8, 1)]
    [InlineData(0, 10, 10, 10, 2)]
    [InlineData(10, 0.005, 10, 9.995, 1)]
    [InlineData(2, 0.5, 6, 0.5, -1)]
    [InlineData(8, 0, 10, 2, -1)]
    public void EdgeContaining_Should_FindTheEdgeALineLiesOn_When_Checked(double ax, double ay, double bx, double by, int edge)
    {
        Geometry.EdgeContaining(Ring("0 0,10 0,10 10,0 10"), new FloorPoint(ax, ay), new FloorPoint(bx, by)).Should().Be(edge);
    }

    [Theory]
    [InlineData("0 0,1 1,2 2", 3)]
    [InlineData(" 0.5 1.25 , 2 3 ", 2)]
    [InlineData("1e1 2", 1)]
    public void ParseRing_Should_ReadInvariantPoints_When_TextIsWellFormed(string text, int count)
    {
        Geometry.ParseRing(text, 200).Should().HaveCount(count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0 0,1")]
    [InlineData("0,5 1")]
    [InlineData("a b")]
    [InlineData("NaN 1")]
    [InlineData("Infinity 1")]
    [InlineData("0 0 0")]
    public void ParseRing_Should_ReturnNull_When_TextIsMalformed(string text)
    {
        Geometry.ParseRing(text, 200).Should().BeNull();
    }

    [Fact]
    public void ParseRing_Should_ReturnNull_When_ThereAreTooManyPoints()
    {
        var text = string.Join(",", Enumerable.Range(0, 201).Select(i => $"{i} 0"));

        Geometry.ParseRing(text, 200).Should().BeNull();
    }

    [Fact]
    public void FormatRing_Should_RoundToTheMillimetre_When_Formatted()
    {
        Geometry.FormatRing([new FloorPoint(1.23449, 2.0005), new FloorPoint(-0.0004, 10)]).Should().Be("1.234 2.001,0.000 10.000");
    }

    #region SegmentsIntersect, Overlaps and IsSimplePolygon in every order (ARV-161)

    /// <summary>The eight ways to pass two segments: either first, each in either direction.</summary>
    private static IEnumerable<(FloorPoint, FloorPoint, FloorPoint, FloorPoint)> EveryOrder(FloorPoint p1, FloorPoint p2, FloorPoint q1, FloorPoint q2) =>
    [
        (p1, p2, q1, q2), (p2, p1, q1, q2), (p1, p2, q2, q1), (p2, p1, q2, q1),
        (q1, q2, p1, p2), (q2, q1, p1, p2), (q1, q2, p2, p1), (q2, q1, p2, p1)
    ];

    /// <summary>Every rotation of the ring, forwards and reversed.</summary>
    private static IEnumerable<IReadOnlyList<FloorPoint>> EveryStart(IReadOnlyList<FloorPoint> ring) =>
        Enumerable.Range(0, ring.Count).SelectMany(k => new IReadOnlyList<FloorPoint>[]
        {
            [.. ring.Skip(k), .. ring.Take(k)],
            [.. ring.Reverse().Skip(k), .. ring.Reverse().Take(k)]
        });

    [Theory]
    [InlineData(0, 0, 10, 10, 0, 10, 10, 0, true, "a proper crossing")]
    [InlineData(0, 0, 10, 0, 5, 0, 15, 0, true, "a collinear overlap")]
    [InlineData(0, 0, 10, 0, 10, 0, 10, 5, true, "a shared end")]
    [InlineData(0, 0, 10, 0, 0, 1, 10, 1, false, "parallel, a metre apart")]
    [InlineData(0, 0, 10, 0, 11, 0, 15, 0, false, "collinear, a metre apart")]
    // The CsCheck seed cCcbgdLcNjA6 of FormulaPropertyTests: segments sharing an end at the origin (3.5e-14 m apart),
    // which the exact-zero cross product answered both ways depending on the order.
    [InlineData(-5.0600000000000006E-83, -4.3010000000000003E-94, 3.6999999999999995E-73, -48.828816836953365,
        -3.486E-14, -3.572E-49, -27.536112694780883, 28.41181609208445, true, "a shared end at the origin (seed cCcbgdLcNjA6)")]
    [InlineData(0.1, 0.2, 0.7, 1.4, 0.3, 0.6, 1.1, 2.2, true, "a collinear overlap on a slanted line, in decimals binary cannot hold")]
    [InlineData(0.1, 0.2, 0.7, 1.4, 0.3, 0.6, 5, -3, true, "an end on the other segment, in decimals binary cannot hold")]
    [InlineData(1000.1, 2000.3, 1000.7, 2001.5, 1000.3, 2000.7, 999, 2003, true, "an end on the other segment, a kilometre from the origin")]
    [InlineData(0, 0, 10, 0, 5, 0.009, 5, 3, true, "an end 9 mm from the other segment touches it")]
    [InlineData(0, 0, 10, 0, 5, 0.011, 5, 3, false, "an end 11 mm from the other segment does not")]
    [InlineData(0, 0, 10, 0, 10.009, 0, 15, 0, true, "collinear, 9 mm apart")]
    [InlineData(0, 0, 10, 0, 10.011, 0, 15, 0, false, "collinear, 11 mm apart")]
    [InlineData(0, 0, 10, 0, 0, 0.02, 10, 0.02, false, "parallel, 2 cm apart")]
    [InlineData(0, -0.5, 100, 0.5, 0, 0.5, 100, -0.5, true, "a shallow proper crossing in the middle")]
    [InlineData(11, 0.009, -200, -1, 0, 0, 10, 0, true, "an end 9 mm from the other's line beyond its end, crossing it at x 9.1")]
    [InlineData(12, 0.005, 20, 3, 0, 0, 10, 0, false, "an end 5 mm from the other's line beyond its end, going away")]
    [InlineData(5, 0, 5, 0, 0, 0, 10, 0, true, "a segment of no length on the other")]
    [InlineData(5, 1, 5, 1, 0, 0, 10, 0, false, "a segment of no length a metre away")]
    [InlineData(1, 1, 1, 1, 1, 1, 1, 1, true, "two segments of no length at one point")]
    [InlineData(1, 1, 1, 1, 1, 1.02, 1, 1.02, false, "two segments of no length 2 cm apart")]
    public void SegmentsIntersect_Should_GiveOneAnswer_InEveryOrderAndDirection_When_Checked(double a, double b, double c, double d, double e, double f,
        double g, double h, bool expected, string because)
    {
        var (p1, p2, q1, q2) = (new FloorPoint(a, b), new FloorPoint(c, d), new FloorPoint(e, f), new FloorPoint(g, h));

        foreach (var (w, x, y, z) in EveryOrder(p1, p2, q1, q2))
            Geometry.SegmentsIntersect(w, x, y, z).Should().Be(expected, "{0}, passed as {1} {2} and {3} {4}", because, w, x, y, z);
    }

    [Theory]
    [InlineData("0 0,10 0,10 10,0 10", true, "square")]
    [InlineData("0 0,10 0,10 10,5 3,0 10", true, "concave arrow")]
    [InlineData("0 0,4 0,4 4,8 4,8 0,12 0,12 8,0 8", true, "U shape: collinear edges 4 m apart")]
    [InlineData("0 0,10 10,10 0,0 10", false, "bow tie: two edges cross")]
    [InlineData("0 0,10 0,10 10,0 10,5 0", false, "vertex touching another edge")]
    [InlineData("0 0,2 0,0.3 0.6,1.5 2.5,0.8 1.6", false, "vertex on another edge, in decimals binary cannot hold")]
    [InlineData("0 0,10 0,10 10,5 0.005,0 10", false, "vertex 5 mm from another edge: within a centimetre, it touches")]
    [InlineData("0 0,10 0,10 10,5 0.02,0 10", true, "vertex 2 cm from another edge")]
    [InlineData("0 0,10 0,10 10,10.001 5,10 15,0 10", false, "a spike folding back 1 mm beside an earlier edge")]
    // ARV-170, the minimum width: every vertex at least 1 cm from each edge it is not part of.
    [InlineData("0 0,10 0,5 0.001", false, "a triangle 10 m long and 1 mm wide (the owner's sliver)")]
    [InlineData("0 0,10 0,5 0.009", false, "a triangle 9 mm wide")]
    [InlineData("0 0,10 0,5 0.011", true, "a triangle 11 mm wide")]
    [InlineData("0 0,10 0,5 0.01", true, "a triangle exactly 1 cm wide")]
    [InlineData("0 0,10 0,0.5 0.009", false, "a triangle 9 mm wide near one end")]
    [InlineData("0 0,10 0,9.995 0.009", false, "a triangle 9 mm wide, its apex almost over a base end")]
    [InlineData("0 0,10 0,10 0.011,0 0.011", true, "a strip 11 mm wide")]
    [InlineData("0 0,10 0,10.02 0.009,0.02 0.009", false, "a slanted strip 9 mm wide, its corners 2.2 cm apart")]
    [InlineData("0 0,10 0,10.02 0.011,0.02 0.011", true, "a slanted strip 11 mm wide, its corners 2.3 cm apart")]
    [InlineData("0 0,10 0,10 0.011", true, "a taper to a sharp corner, 11 mm at its widest: no vertex within 1 cm of an edge it is not part of")]
    [InlineData("0 0,10 0,10 10,5 0.009,0 10", false, "a notch reaching 9 mm from the far edge")]
    [InlineData("0 0,10 0,10 10,5 0.011,0 10", true, "a notch reaching 11 mm from the far edge")]
    [InlineData("10 10,13 10,13 12,10 12", true, "a real zone, 3 m by 2 m")]
    [InlineData("10 10,16 10,16 11,11 11,11 14,10 14", true, "a real L-shaped snake")]
    public void IsSimplePolygon_Should_GiveOneAnswer_When_TheRingStartsElsewhereOrRunsBackwards(string ring, bool simple, string because)
    {
        foreach (var turned in EveryStart(Ring(ring)))
            Geometry.IsSimplePolygon(turned).Should().Be(simple, "{0}, as {1}", because, Geometry.FormatRing(turned));
    }

    [Theory]
    [InlineData("0.5 0.2,0.9 0.2,0.9 0.3,0.5 0.3", true, "crossing the zone's edge")]
    [InlineData("0.2 0.2,0.3 0.2,0.3 0.3", true, "inside the zone")]
    [InlineData("0.7 0.2,1.3 0.2,1.3 0.3,0.7 0.3", true, "an edge along the zone's edge")]
    [InlineData("0.7 0.4,1 0.7,0.7 1,0.4 0.7", true, "a corner on the zone's corner")]
    [InlineData("0.705 0.2,1.3 0.2,1.3 0.3,0.705 0.3", true, "5 mm from the zone: within a centimetre, it touches")]
    [InlineData("0.72 0.2,1.3 0.2,1.3 0.3,0.72 0.3", false, "2 cm from the zone")]
    public void Overlaps_Should_GiveOneAnswer_When_EitherRingStartsElsewhereOrRunsBackwards(string footprint, bool expected, string because)
    {
        var zone = Ring("0.1 0.1,0.7 0.1,0.7 0.4,0.1 0.4");

        foreach (var first in EveryStart(Ring(footprint)))
        {
            foreach (var second in EveryStart(zone))
            {
                Geometry.Overlaps(first, second).Should().Be(expected, "{0}, as {1} and {2}", because, Geometry.FormatRing(first), Geometry.FormatRing(second));
                Geometry.Overlaps(second, first).Should().Be(expected, "{0}, the zone first", because);
            }
        }
    }

    #endregion
}
