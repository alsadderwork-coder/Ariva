using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Sensing;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Sensing;

/// <summary>
/// ARV-021: the BOQ's assumed coverage footprints by mounting height (labelled), vendor footprints, the footprint ring and
/// its overlap with a zone, and the bounds every canonical sensing event is checked against.
/// </summary>
public sealed class SensingModelTests
{
    private static readonly DateTime T = new(2026, 10, 2, 18, 5, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(4, 10, 10, "Assumed, from the BOQ (4 to 6 m)")]
    [InlineData(6, 10, 10, "Assumed, from the BOQ (4 to 6 m)")]
    [InlineData(10, 12, 9, "Assumed, from the BOQ (10 to 14 m)")]
    [InlineData(14, 12, 9, "Assumed, from the BOQ (10 to 14 m)")]
    [InlineData(8, 11, 9.5, "Estimate, interpolated between the BOQ's assumed footprints")]
    [InlineData(3, 7.5, 7.5, "Estimate, scaled from the BOQ's assumed footprints")]
    [InlineData(18, 15.4, 11.6, "Estimate, scaled from the BOQ's assumed footprints")]
    public void Assumed_Should_FollowTheBoqTable_When_TheFamilyHasARectangle(double height, double length, double width, string note)
    {
        var footprint = CoverageFootprint.Assumed(DeviceFamily.StereoVision, height);

        footprint.LengthMetres.Should().Be(length);
        footprint.WidthMetres.Should().Be(width);
        footprint.RadiusMetres.Should().BeNull();
        footprint.Source.Should().Be(FootprintSource.AssumedFromBoq);
        footprint.Note.Should().Be(note);
    }

    [Fact]
    public void Assumed_Should_BeATenMetreRadius_When_TheDeviceIsLidar()
    {
        var footprint = CoverageFootprint.Assumed(DeviceFamily.Lidar, 8);

        footprint.RadiusMetres.Should().Be(10);
        footprint.Text.Should().Be("10 m radius");
        footprint.Note.Should().Contain("Assumed radius");
    }

    [Fact]
    public void FromVendor_Should_TakeEitherARectangleOrARadius()
    {
        CoverageFootprint.FromVendor(9.6, 9.6, null).Should().Match<CoverageFootprint>(f => f.Source == FootprintSource.Vendor && f.Text == "9.6 x 9.6 m");
        CoverageFootprint.FromVendor(null, null, 12).Text.Should().Be("12 m radius");

        var both = () => CoverageFootprint.FromVendor(10, 10, 5);
        var half = () => CoverageFootprint.FromVendor(10, null, null);
        var huge = () => CoverageFootprint.FromVendor(61, 10, null);
        var zero = () => CoverageFootprint.FromVendor(null, null, 0);
        var roundsToNothing = () => CoverageFootprint.FromVendor(0.04, 10, null);
        roundsToNothing.Should().Throw<ArgumentException>("a side is kept to 0.1 m");
        CoverageFootprint.FromVendor(0.05, 10, null).LengthMetres.Should().Be(0.1);
        both.Should().Throw<ArgumentException>();
        half.Should().Throw<ArgumentException>();
        huge.Should().Throw<ArgumentException>();
        zero.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Ring_Should_CentreTheRectangleOnTheSensorAndTurnItWithTheOrientation()
    {
        var footprint = CoverageFootprint.FromVendor(10, 4, null);

        var flat = footprint.Ring(new FloorPoint(20, 10), 0);
        var turned = footprint.Ring(new FloorPoint(20, 10), 90);

        flat.Should().Equal(new FloorPoint(15, 8), new FloorPoint(25, 8), new FloorPoint(25, 12), new FloorPoint(15, 12));
        turned.Select(p => p.Rounded()).Should().Equal(new FloorPoint(22, 5), new FloorPoint(22, 15), new FloorPoint(18, 15), new FloorPoint(18, 5));
    }

    [Fact]
    public void Overlaps_Should_DetectEdgesInsideAndApart()
    {
        IReadOnlyList<FloorPoint> zone = [new(10, 10), new(30, 10), new(30, 20), new(10, 20)];
        var footprint = CoverageFootprint.FromVendor(4, 4, null);

        Geometry.Overlaps(footprint.Ring(new FloorPoint(20, 15), 0), zone).Should().BeTrue("inside the zone");
        Geometry.Overlaps(footprint.Ring(new FloorPoint(11, 9), 0), zone).Should().BeTrue("across the zone's corner");
        Geometry.Overlaps(CoverageFootprint.FromVendor(60, 60, null).Ring(new FloorPoint(20, 15), 0), zone).Should().BeTrue("around the whole zone");
        Geometry.Overlaps(footprint.Ring(new FloorPoint(40, 15), 0), zone).Should().BeFalse("beside the zone");
        Geometry.Overlaps(CoverageFootprint.FromVendor(null, null, 3).Ring(new FloorPoint(35, 15), 0), zone).Should().BeFalse("a circle that stops short");
        Geometry.Overlaps(CoverageFootprint.FromVendor(null, null, 6).Ring(new FloorPoint(35, 15), 0), zone).Should().BeTrue("a circle that reaches");
    }

    [Theory]
    [InlineData(20, 15, true)]
    [InlineData(10, 15, true)]
    [InlineData(30, 20, true)]
    [InlineData(9.9, 15, false)]
    [InlineData(20, 25, false)]
    public void ContainsPoint_Should_IncludeTheBoundary(double x, double y, bool inside)
    {
        IReadOnlyList<FloorPoint> zone = [new(10, 10), new(30, 10), new(30, 20), new(10, 20)];

        Geometry.ContainsPoint(zone, new FloorPoint(x, y)).Should().Be(inside);
    }

    public static TheoryData<CanonicalEvent> ValidEvents() =>
    [
        new TrackPosition("7", 12.5, 8.25, 1.72, T),
        new TrackPosition("trk-0001_a.b", 0, 0, null, T),
        new LineCrossing("A-VIS entry", CrossingDirection.In, "7", T),
        new LineCrossing("A-VIS exit", CrossingDirection.Out, null, T),
        new ZoneOccupancy("A-VIS", 41, T),
        new IntervalCount("A-VIS entry", 30, 2, T.AddMinutes(-5), T),
        new DeviceStatus(true, 41.5, 12.5, -18, T),
        new DeviceStatus(false, null, null, null, T)
    ];

    [Theory]
    [MemberData(nameof(ValidEvents))]
    public void Validate_Should_AcceptEvents_When_WithinTheBounds(CanonicalEvent value) =>
        CanonicalEventRules.Validate(value).Should().BeEmpty();

    public static TheoryData<CanonicalEvent, string> InvalidEvents() => new()
    {
        { new TrackPosition("7", double.NaN, 1, null, T), "x coordinate" },
        { new TrackPosition("7", 1, 1e9, null, T), "y coordinate" },
        { new TrackPosition("7", 1, 1, 7, T), "height" },
        { new TrackPosition(null, 1, 1, null, T), "track id is required" },
        { new TrackPosition("../../etc", 1, 1, null, T), "track id" },
        { new TrackPosition(new string('a', 65), 1, 1, null, T), "track id" },
        { new TrackPosition("7\n", 1, 1, null, T), "track id" },
        { new TrackPosition("7", 1, 1, null, DateTime.SpecifyKind(T, DateTimeKind.Local)), "UTC" },
        { new TrackPosition("7", 1, 1, null, default), "UTC" },
        { new LineCrossing("", CrossingDirection.In, null, T), "line name" },
        { new LineCrossing("Entry‮", CrossingDirection.In, null, T), "line name" },
        { new LineCrossing("Entry", (CrossingDirection)7, null, T), "direction" },
        { new ZoneOccupancy("A-VIS", -1, T), "Occupancy" },
        { new ZoneOccupancy("A-VIS", 10_001, T), "Occupancy" },
        { new IntervalCount("Entry", 1, 1, T, T), "interval" },
        { new IntervalCount("Entry", 1, 1, T.AddDays(-2), T), "interval" },
        { new IntervalCount("Entry", -1, 1, T.AddMinutes(-1), T), "Interval counts" },
        { new DeviceStatus(true, 500, null, null, T), "temperature" },
        { new DeviceStatus(true, null, -1, null, T), "frame rate" },
        { new DeviceStatus(true, null, null, double.PositiveInfinity, T), "clock offset" }
    };

    [Theory]
    [MemberData(nameof(InvalidEvents))]
    public void Validate_Should_NameTheProblem_When_AnEventBreaksABound(CanonicalEvent value, string problem) =>
        CanonicalEventRules.Validate(value).Should().Contain(p => p.Contains(problem, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void NamespacedTrackId_Should_PrefixTheDevice() =>
        CanonicalEventRules.NamespacedTrackId("S-17", "7").Should().Be("S-17/7");
}
