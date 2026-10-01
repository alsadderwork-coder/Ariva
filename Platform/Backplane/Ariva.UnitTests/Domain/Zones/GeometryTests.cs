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

    [Theory]
    [InlineData(0, 0, 10, 10, 0, 10, 10, 0, true)]
    [InlineData(0, 0, 10, 0, 5, 0, 15, 0, true)]
    [InlineData(0, 0, 10, 0, 10, 0, 10, 5, true)]
    [InlineData(0, 0, 10, 0, 0, 1, 10, 1, false)]
    [InlineData(0, 0, 10, 0, 11, 0, 15, 0, false)]
    public void SegmentsIntersect_Should_IncludeTouching_When_Checked(double a, double b, double c, double d, double e, double f, double g, double h, bool expected)
    {
        Geometry.SegmentsIntersect(new FloorPoint(a, b), new FloorPoint(c, d), new FloorPoint(e, f), new FloorPoint(g, h)).Should().Be(expected);
    }
}
