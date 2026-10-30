using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.Events;
using Ariva.Core.Sensing;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Sensing;

/// <summary>
/// ARV-259e4 (ADR-0027): a device declares which entry and exit lines of its queue zone it counts: All (the default), Listed
/// (1 to 16 distinct names by the name rule, kept in ordinal order) or None. A declaration never recommissions the device and
/// raises CoverageChanged once; a refused one changes nothing; a device assigned to another zone that listed its lines counts
/// every line again. <see cref="LineDeclarations"/> reads declarations against a zone's lines.
/// </summary>
public sealed class DeviceLineCoverageTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);

    private static Device NewDevice()
    {
        var level = new Airport("DMO", null, "Demo", "Asia/Dubai").AddTerminal("T1", "T1", "DMO").AddLevel("ARR", "Arrivals", 0, 100, 60);
        level.Id = Guid.Parse("0199a000-0000-7000-8000-0000000000a1");
        return new Device("S-17", DeviceFamily.StereoVision, "PC2SE", DeviceTransport.HttpsPush, DeviceDialect.Xovis, ClockSource.Ntp, level, 30, 20, 5, 0,
            CoverageFootprint.Assumed(DeviceFamily.StereoVision, 5), "A-VIS", Now);
    }

    [Fact]
    public void CoverLines_Should_KeepTheListedLinesInOrderAndNeverRecommission_When_Declared()
    {
        var device = NewDevice();
        device.LineCoverage.Should().Be(DeviceLineCoverage.All, "a device that declares nothing counts every line");
        device.CoveredLineNames.Should().BeEmpty();
        device.RecordCalibration(CalibrationMethod.ManualCountTally, 200, 98, 0.3, 95, null, true, Now);

        device.CoverLines(DeviceLineCoverage.Listed, [" Exit A ", "Entry B", "Entry A"], Now).Should().BeTrue();

        device.LineCoverage.Should().Be(DeviceLineCoverage.Listed);
        device.CoveredLineNames.Should().Equal("Entry A", "Entry B", "Exit A");
        device.CoveredLines.Should().Be("Entry A\nEntry B\nExit A");
        device.State.Should().Be(DeviceState.Online, "what it measures is unchanged");
        device.DomainEvents.OfType<DeviceRegistryChanged>().Last().Should().Match<DeviceRegistryChanged>(e => e.Change == "CoverageChanged" && e.State == "Online");

        var events = device.DomainEvents.Count;
        device.CoverLines(DeviceLineCoverage.Listed, ["Exit A", "Entry A", "Entry B"], Now).Should().BeFalse("the same lines in another order");
        device.DomainEvents.Should().HaveCount(events);

        device.CoverLines(DeviceLineCoverage.None, null, Now).Should().BeTrue();
        (device.LineCoverage, device.CoveredLines).Should().Be((DeviceLineCoverage.None, (string)null));
        device.CoverLines(DeviceLineCoverage.All, [], Now).Should().BeTrue();
        (device.LineCoverage, device.CoveredLines).Should().Be((DeviceLineCoverage.All, (string)null));
        device.DomainEvents.Should().HaveCount(events + 2);
    }

    [Theory]
    [InlineData("Listed", 0, null, "no line")]
    [InlineData("Listed", 17, null, "more than 16")]
    [InlineData("Listed", 1, "", "an empty name")]
    [InlineData("Listed", 1, "   ", "a blank name")]
    [InlineData("Listed", 1, "Entry\u202EA", "a right-to-left override")]
    [InlineData("Listed", 1, "Entry\nA", "a line feed, the separator")]
    [InlineData("Listed", 1, "Entry\u200BA", "a zero-width space")]
    [InlineData("Listed", 2, "Entry A", "a line listed twice")]
    [InlineData("All", 1, "Entry A", "All names no line")]
    [InlineData("None", 1, "Entry A", "None names no line")]
    public void CoverLines_Should_ChangeNothing_When_TheDeclarationIsRefused(string coverage, int count, string name, string because)
    {
        var device = NewDevice();
        device.CoverLines(DeviceLineCoverage.Listed, ["Exit A"], Now);
        var events = device.DomainEvents.Count;
        IReadOnlyList<string> lines = name is null ? [.. Enumerable.Range(0, count).Select(i => $"Line {i}")] : [.. Enumerable.Repeat(name, count)];

        var act = () => device.CoverLines(Enum.Parse<DeviceLineCoverage>(coverage), lines, Now);

        act.Should().Throw<ArgumentException>(because);
        device.CoveredLineNames.Should().Equal(["Exit A"], because);
        device.DomainEvents.Should().HaveCount(events);
    }

    [Fact]
    public void CoverLines_Should_RefuseALongNameAnUnknownKindAndARetiredDevice()
    {
        var device = NewDevice();

        FluentActions.Invoking(() => device.CoverLines(DeviceLineCoverage.Listed, [new string('a', 201)], Now)).Should().Throw<ArgumentException>();
        device.CoverLines(DeviceLineCoverage.Listed, [new string('a', 200), .. Enumerable.Range(0, 15).Select(i => new string((char)('b' + i), 200))], Now)
            .Should().BeTrue("16 names of 200 characters fit the column");
        device.CoveredLines.Length.Should().BeLessThanOrEqualTo(Device.CoveredLinesLength);
        FluentActions.Invoking(() => device.CoverLines((DeviceLineCoverage)7, [], Now)).Should().Throw<ArgumentOutOfRangeException>();

        device.Retire(Now);
        FluentActions.Invoking(() => device.CoverLines(DeviceLineCoverage.All, [], Now)).Should().Throw<InvalidOperationException>().WithMessage("*retired*");
    }

    [Fact]
    public void Move_Should_CountEveryLineAgain_When_ADeviceThatListedItsLinesMovesToAnotherZone()
    {
        var device = NewDevice();
        device.CoverLines(DeviceLineCoverage.Listed, ["Exit A"], Now);

        device.Move(device.Level, 31, 20, 5, 0, device.Footprint, "A-VIS", Now);
        device.CoveredLineNames.Should().Equal(["Exit A"], "the same zone keeps its lines");

        device.Move(device.Level, 31, 20, 5, 0, device.Footprint, "A-CIT", Now);
        (device.LineCoverage, device.CoveredLines).Should().Be((DeviceLineCoverage.All, (string)null), "the names were the old zone's");

        device.CoverLines(DeviceLineCoverage.None, [], Now);
        device.Move(device.Level, 31, 20, 5, 0, device.Footprint, "A-VIS", Now);
        device.LineCoverage.Should().Be(DeviceLineCoverage.None, "a device that counts no queue line still counts none");
    }

    [Theory]
    [InlineData("All", "", "Entry A,Entry B,Exit A", "")]
    [InlineData("Listed", "Exit A", "Exit A", "")]
    [InlineData("Listed", "Entry A,Gone", "Entry A", "Gone")]
    [InlineData("Listed", "Gone,Renamed", "", "Gone,Renamed")]
    [InlineData("None", "", "", "")]
    public void LineDeclarations_Should_ReadADeclarationAgainstTheZonesLines(string coverage, string listed, string covered, string notInZone)
    {
        var zoneLines = new HashSet<string>(["Entry A", "Entry B", "Exit A"], StringComparer.Ordinal);
        var kind = Enum.Parse<DeviceLineCoverage>(coverage);
        IReadOnlyList<string> names = listed.Length == 0 ? [] : listed.Split(',');

        LineDeclarations.Covered(kind, names, zoneLines).Order(StringComparer.Ordinal).Should().Equal(covered.Length == 0 ? [] : covered.Split(','));
        LineDeclarations.NotInZone(kind, names, zoneLines).Should().Equal(notInZone.Length == 0 ? [] : notInZone.Split(','));
    }

    [Fact]
    public void LineDeclarations_Should_ListTheZonesLinesNoDeclarationCovers()
    {
        var zoneLines = new HashSet<string>(["Entry A", "Entry B", "Exit A"], StringComparer.Ordinal);

        LineDeclarations.Uncovered(zoneLines, []).Should().Equal(["Entry A", "Entry B", "Exit A"], "no device: every line");
        LineDeclarations.Uncovered(zoneLines, [(DeviceLineCoverage.All, [])]).Should().BeEmpty();
        LineDeclarations.Uncovered(zoneLines, [(DeviceLineCoverage.None, []), (DeviceLineCoverage.Listed, ["Entry B", "Gone"])])
            .Should().Equal(["Entry A", "Exit A"], "a name the zone does not hold covers nothing");
        LineDeclarations.Uncovered(zoneLines, [(DeviceLineCoverage.Listed, ["Entry A", "Entry B"]), (DeviceLineCoverage.Listed, ["Exit A"])]).Should().BeEmpty();
        LineDeclarations.Uncovered(zoneLines, [(DeviceLineCoverage.Listed, ["entry a"])]).Should().Contain("Entry A", "names compare ordinally");
        FluentActions.Invoking(() => LineDeclarations.Uncovered(null, [])).Should().Throw<ArgumentNullException>();
    }
}
