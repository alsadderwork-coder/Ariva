using Ariva.Core;
using Ariva.Core.Security;
using Ariva.Core.Services.Live;
using FluentAssertions;

namespace Ariva.UnitTests.Live;

/// <summary>
/// ARV-120: the overflow band states the live and immigration screens mark. A band is Occupied when its latest minute with a
/// reading in the window held anyone (TC-19), Empty when that minute's highest reading was 0, NotHeard without a reading in
/// the window (never read as empty); a queue zone is Occupied when any band is, Empty only when every band reported empty.
/// </summary>
public sealed class OverflowBandStatesTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 18, 10, 30, DateTimeKind.Utc);
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(ISvcOverflowBands.FreshMinutes);

    private static OverflowBandReading Band(string zone, string band, double? minutesAgo, int? occupancy) =>
        new(zone, band, minutesAgo is { } m ? Now.AddMinutes(-m) : null, occupancy);

    [Theory]
    [InlineData(null, OverflowBandStates.NotHeard)]
    [InlineData(0, OverflowBandStates.Empty)]
    [InlineData(1, OverflowBandStates.Occupied)]
    [InlineData(12, OverflowBandStates.Occupied)]
    [InlineData(10_000, OverflowBandStates.Occupied)]
    [InlineData(10_001, OverflowBandStates.NotHeard)]
    [InlineData(-1, OverflowBandStates.NotHeard)]
    [InlineData(int.MinValue, OverflowBandStates.NotHeard)]
    [InlineData(int.MaxValue, OverflowBandStates.NotHeard)]
    public void Of_Should_ReadTheLatestReading_When_ItIsInTheTablesRange(int? occupancy, string expected) =>
        OverflowBandStates.Of(occupancy).Should().Be(expected);

    [Theory]
    [InlineData(new[] { "Occupied" }, "Occupied")]
    [InlineData(new[] { "Empty", "Occupied" }, "Occupied")]
    [InlineData(new[] { "NotHeard", "Occupied" }, "Occupied")]
    [InlineData(new[] { "Empty" }, "Empty")]
    [InlineData(new[] { "Empty", "Empty" }, "Empty")]
    [InlineData(new[] { "Empty", "NotHeard" }, "NotHeard")]
    [InlineData(new[] { "NotHeard" }, "NotHeard")]
    [InlineData(new string[0], "NotHeard")]
    [InlineData(new[] { "occupied" }, "NotHeard")]
    public void OfZone_Should_BeOccupiedWhenAnyBandIs_When_EmptyNeedsEveryBandHeard(string[] bands, string expected) =>
        OverflowBandStates.OfZone(bands).Should().Be(expected);

    [Fact]
    public void Build_Should_GroupBandsByQueueZoneInNameOrder_When_TheReaderGivesThemInAnyOrder()
    {
        var zones = OverflowBandStates.Build(
        [
            Band("SEC-N", "SEC-OV", 3, 0),
            Band("A-VIS", "A-OV", 2, 12),
            Band("CI-A", "CI-A-OV", null, null),
            Band("A-VIS", "A-OV2", 2.5, 0)
        ], Now, Fresh);

        zones.Select(z => z.Zone).Should().Equal("A-VIS", "CI-A", "SEC-N");
        zones[0].Should().BeEquivalentTo(new OverflowZoneViewModel("A-VIS", "Occupied",
        [
            new OverflowBandViewModel("A-OV", "Occupied", Now.AddMinutes(-2), 12),
            new OverflowBandViewModel("A-OV2", "Empty", Now.AddMinutes(-2.5), 0)
        ]), o => o.WithStrictOrdering());
        zones[1].Should().BeEquivalentTo(new OverflowZoneViewModel("CI-A", "NotHeard", [new OverflowBandViewModel("CI-A-OV", "NotHeard", null, null)]));
        zones[2].State.Should().Be("Empty");
    }

    [Theory]
    [InlineData(0, OverflowBandStates.Occupied)]
    [InlineData(6, OverflowBandStates.Occupied)]
    [InlineData(6.001, OverflowBandStates.NotHeard)]
    [InlineData(45, OverflowBandStates.NotHeard)]
    [InlineData(-0.001, OverflowBandStates.NotHeard)]
    [InlineData(-60, OverflowBandStates.NotHeard)]
    public void Build_Should_CountOnlyAReadingWithinTheWindow_When_TheReaderReturnedAnother(double minutesAgo, string expected)
    {
        var band = OverflowBandStates.Build([Band("A-VIS", "A-OV", minutesAgo, 9)], Now, Fresh).Single().Bands.Single();

        band.State.Should().Be(expected);
        if (expected == OverflowBandStates.NotHeard)
        {
            band.MinuteUtc.Should().BeNull("a reading outside the window is not shown");
            band.MaxOccupancy.Should().BeNull();
        }
    }

    [Fact]
    public void Build_Should_KeepTheFirstReadingOfABand_When_TheReaderRepeatsIt()
    {
        var zones = OverflowBandStates.Build([Band("A-VIS", "A-OV", 1, 0), Band("A-VIS", "A-OV", 0.5, 30)], Now, Fresh);

        zones.Single().Bands.Should().ContainSingle().Which.State.Should().Be("Empty");
    }

    [Fact]
    public void Build_Should_LeaveOutRowsWithoutAZoneOrBand_When_TheyComeFromAWriterGoneWrong()
    {
        var zones = OverflowBandStates.Build([null, Band("", "A-OV", 1, 3), Band("A-VIS", "", 1, 3), Band(null, "A-OV", 1, 3), Band("A-VIS", null, 1, 3)],
            Now, Fresh);

        zones.Should().BeEmpty();
    }

    [Fact]
    public void Build_Should_ReturnNothing_When_TheProfileHasNoBand() =>
        OverflowBandStates.Build([], Now, Fresh).Should().BeEmpty();

    [Fact]
    public void Build_Should_KeepNamesAsText_When_TheyHoldMarkup()
    {
        // Names are data: the screens render them as text (CWE-79); the builder neither drops nor rewrites them.
        const string zone = "<img src=x onerror=alert(1)>";
        var built = OverflowBandStates.Build([Band(zone, "\"><svg/onload=alert(1)>", 1, 4)], Now, Fresh).Single();

        built.Zone.Should().Be(zone);
        built.Bands.Single().Band.Should().Be("\"><svg/onload=alert(1)>");
    }

    /// <summary>Bands of queue zones three to a zone, in the reader's order (zone, then band).</summary>
    private static List<OverflowBandReading> Bands(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Band($"Z-{i / 3:D4}", $"B-{i:D4}", 1, i % 2))];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(499)]
    [InlineData(500)]
    public void Within_Should_KeepEveryBandWhole_When_TheyAreAtMostTheBound(int count)
    {
        var rows = Bands(count);

        var within = OverflowBandStates.Within(rows);

        within.Should().BeSameAs(rows);
        var built = OverflowBandStates.Build(within, Now, Fresh);
        built.Sum(z => z.Bands.Count).Should().Be(count, "every band the reader returned is in the answer");
        built.Should().HaveCount((count + 2) / 3);
    }

    [Theory]
    [InlineData(501)]
    [InlineData(502)]
    [InlineData(10_000)]
    public void Within_Should_RefuseTheRead_When_TheBandsAreMoreThanTheBound(int count) =>
        OverflowBandStates.Within(Bands(count)).Should().BeNull("beyond the bound the read is refused, never cut short");

    [Fact]
    public void Within_Should_NeverBuildAZoneFromPartOfItsBands_When_TheBoundFallsInsideAZone()
    {
        // 501 bands, three to a zone: a cut at 500 would keep two of zone Z-0166's three bands and call it Empty or Occupied
        // from them. Refused instead, nothing is built; at 500 the last zone is whole (Z-0166 has bands 498 and 499).
        OverflowBandStates.Within(Bands(501)).Should().BeNull();
        var last = OverflowBandStates.Build(OverflowBandStates.Within(Bands(500)), Now, Fresh)[^1];
        last.Zone.Should().Be("Z-0166");
        last.Bands.Select(b => b.Band).Should().Equal("B-0498", "B-0499");
        ISvcOverflowBands.MaxBands.Should().Be(500);
    }

    [Fact]
    public void Within_Should_Throw_When_TheReaderGaveNoList() =>
        FluentActions.Invoking(() => OverflowBandStates.Within(null)).Should().Throw<ArgumentNullException>();

    [Fact]
    public void Overflow_Should_BeLiveQueueData_When_RolesAreSeeded()
    {
        // The endpoint asks for LiveQueue.View, as the live hub's zone snapshots: every operational role but the observer.
        var permission = Global.Defaults.Permissions.ViewLiveQueue;
        RolePermissions.BorderShiftSupervisor.Should().Contain(permission);
        RolePermissions.TerminalDutyManager.Should().Contain(permission);
        RolePermissions.HandlerStationManager.Should().Contain(permission);
        RolePermissions.SystemAdministrator.Should().Contain(permission);
        RolePermissions.ValidationObserver.Should().NotContain(permission);
        // Every holder of the immigration screen may read the bands its lanes are marked with.
        foreach (var role in RolePermissions.ByRole.Values.Where(r => r.Contains(Global.Defaults.Permissions.ViewImmigration)))
            role.Should().Contain(permission);
    }
}
