using Ariva.Core.Flights;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Flights;

/// <summary>
/// ARV-185: which categories of the arrival wave a queue zone's lane takes, one rule for the staffing recommendation and the
/// predicted-breach wave (owner decision 2026-10-26): a lane the wave models takes its own category; the shared lane ALL takes
/// the manual categories that no arrivals-hall queue of the site has as its own lane; any other lane takes none.
/// </summary>
public sealed class LaneDemandTests
{
    public static TheoryData<string, string[], string[]> Lanes => new()
    {
        { "VIS", [], ["VIS"] },
        { "VIS", ["VIS", "ALL"], ["VIS"] },
        { "CRW", ["ALL"], ["CRW"] },
        { "EG", ["EG", "ALL"], ["EG"] },
        // The NBJ seed's halls: one shared queue, nothing else.
        { "ALL", ["ALL"], ["CIT", "CRW", "RES", "VIS"] },
        { "ALL", [], ["CIT", "CRW", "RES", "VIS"] },
        // A visitors queue of its own: the shared queue takes the rest.
        { "ALL", ["ALL", "VIS"], ["CIT", "CRW", "RES"] },
        // The e-gate queue takes none of the manual categories from the shared queue.
        { "ALL", ["ALL", "EG"], ["CIT", "CRW", "RES", "VIS"] },
        { "ALL", ["ALL", "CIT", "RES", "VIS", "CRW"], [] },
        { "ALL", ["ALL", null, "FT"], ["CIT", "CRW", "RES", "VIS"] },
        // A site's own code, and lower case: the wave models neither.
        { "FT", ["FT"], [] },
        { "all", ["all"], [] },
        { "vis", ["vis"], [] },
        { null, ["VIS"], [] }
    };

    [Theory]
    [MemberData(nameof(Lanes))]
    public void Categories_Should_TakeTheLanesOwnOrTheSharedLanesUncovered_When_Asked(string lane, string[] hallLanes, string[] expected)
    {
        LaneDemand.Categories(lane, hallLanes).Should().Equal(expected);
    }

    [Fact]
    public void Categories_Should_Throw_When_TheHallLanesAreNull()
    {
        FluentActions.Invoking(() => LaneDemand.Categories("ALL", null)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Of_Should_SumTheCategories_When_EveryValueIsPlausible()
    {
        var lanes = new LaneCounts(10, 20, 30, 4, 50);

        LaneDemand.Of(lanes, ["CIT", "CRW", "RES", "VIS"]).Should().Be(64);
        LaneDemand.Of(lanes, ["EG"]).Should().Be(50);
        LaneDemand.Of(lanes, []).Should().Be(0);
        LaneDemand.Of(lanes, ["FT"]).Should().Be(0, "a code the wave does not model has none");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.001)]
    public void Of_Should_GiveNaN_When_ATakenCategoryIsNotPlausible(double value)
    {
        // CWE-501: the minute is poisoned for the lanes that take the category, not for the others.
        var lanes = new LaneCounts(10, 20, value, 4, 50);

        LaneDemand.Of(lanes, ["CIT", "VIS"]).Should().Be(double.NaN);
        LaneDemand.Of(lanes, ["CIT", "RES"]).Should().Be(30);
    }

    [Fact]
    public void Of_Should_GiveNaN_When_TheSumOverflows()
    {
        var lanes = new LaneCounts(double.MaxValue, double.MaxValue, 0, 0, 0);

        LaneDemand.Of(lanes, ["CIT", "RES"]).Should().Be(double.NaN, "two finite values whose sum is not finite");
        LaneDemand.Of(lanes, ["CIT"]).Should().Be(double.MaxValue);
    }

    [Fact]
    public void Of_Should_Throw_When_AnArgumentIsNull()
    {
        FluentActions.Invoking(() => LaneDemand.Of(null, ["CIT"])).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => LaneDemand.Of(new LaneCounts(0, 0, 0, 0, 0), null)).Should().Throw<ArgumentNullException>();
    }
}
