using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using Ariva.Core.Validation.Comparison;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Validation;

/// <summary>
/// ARV-104g2: the comparison in slices (<see cref="ComparisonSlices"/>). Sliced and pooled, a campaign within the engine's bounds
/// gives the whole comparison value for value (also the generated campaigns of ComparisonPropertyTests); beyond the bound of
/// 1,000,000 queue minutes only the slices compare; the tracer matching rule's minutes reach two minutes either side of a join;
/// rows of no zone in scope are left out and counted as the whole does; and the nowcast's coverage counts the planned minutes
/// with a realised wait and those the published nowcast covered.
/// </summary>
public sealed class ComparisonSlicesTests
{
    #region Setup

    private const int Version = 7;
    private const string Vis = "A-VIS";
    private const string Cit = "A-CIT";
    private static readonly Guid ZoneA = G(1);
    private static readonly Guid ZoneB = G(2);
    private static readonly Guid O1 = G(901);

    private static Guid G(int n) => Guid.Parse($"00000000-0000-7000-8000-{n:D12}");

    private static DateTime At(int hour, int minute, int second = 0) => new(2026, 10, 8, hour, minute, second, DateTimeKind.Utc);

    private static ComparisonScope Scope() =>
        new(Version, [new ScopeZone(ZoneA, Vis), new ScopeZone(ZoneB, Cit)], [new ScopeLine(G(11), "Entry A", LineRole.Entry, Vis)], [new UtcWindow(At(18, 0), At(19, 0))]);

    private static QueueBinRow Bin(string zone, DateTime start) => new(zone, start, TimeSpan.FromMinutes(15), 1, BinStatus.Final, BinQuality.Good, Version, 100, 0);

    private static QueueMinuteRow Row(string zone, DateTime minute, double? nowcast, double? realised, string noService = null, bool live = true) =>
        new(zone, minute, Version, BinStatus.Final, realised is null ? 0 : 10, realised, live ? nowcast : null, live ? noService : null, live ? false : null);

    private static TracerRunRow Run(int n, Guid zone, DateTime joined, double minutes, Guid? batch = null) =>
        new(G(1000 + n), batch ?? G(2000), O1, zone, $"T-{n:D2}", joined, joined.AddMinutes(minutes), 0, joined, joined.AddMinutes(minutes), false);

    /// <summary>Both zones' minutes from 17:55 to 19:05 with nowcasts and realised waits that change every minute, a shadow of each, and good bins.</summary>
    private static ComparisonInput Campaign()
    {
        var minutes = new List<QueueMinuteRow>();
        var shadows = new List<ShadowMinuteRow>();
        foreach (var (zone, k) in new[] { (Vis, 1.0), (Cit, 2.0) })
        {
            for (var m = -5; m <= 65; m++)
            {
                var at = At(18, 0).AddMinutes(m);
                minutes.Add(m % 17 == 0 ? Row(zone, at, null, 4 + (k * Mod(m, 7)), "NothingOpen") : Row(zone, at, 3 + (k * Mod(m, 5)), 4 + (k * Mod(m, 7))));
                shadows.Add(new ShadowMinuteRow(zone, at, 2 + (k * Mod(m, 3)), null, false, null));
            }
        }

        return new ComparisonInput
        {
            Scope = Scope(),
            // One batch over both zones (a subset scope would drop it whole), joins on and off the minute and at a window's edge.
            TracerRuns = [Run(1, ZoneA, At(18, 10, 30), 8), Run(2, ZoneB, At(18, 20, 0), 6), Run(3, ZoneA, At(18, 59, 59), 5), Run(4, ZoneB, At(18, 0, 1), 4)],
            QueueMinutes = minutes,
            ShadowMinutes = shadows,
            QueueBins = [.. new[] { Vis, Cit }.SelectMany(z => Enumerable.Range(-1, 12).Select(i => Bin(z, At(18, 0).AddMinutes(15 * i))))],
            QualityIntervals = [new QualityInterval(Cit, At(18, 40), At(18, 44), BinQuality.Degraded)]
        };
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    private static int Mod(int m, int n) => ((m % n) + n) % n;

    #endregion

    [Fact]
    public void CompareSliced_Should_EqualTheWholeComparison_When_TheCampaignIsWithinTheBounds()
    {
        var input = Campaign();
        var settings = new ComparisonSettings { SensitivityShift = TimeSpan.FromMinutes(1) };

        var whole = ValidationComparison.Compare(input, settings);
        var (sliced, coverage) = ComparisonSlices.CompareSliced(input, settings);

        whole.Tracers.Should().HaveCount(4).And.Contain(t => t.SystemWaitMinutes != null);
        whole.NowcastMinutes.Should().NotBeEmpty();
        Json(sliced).Should().Be(Json(whole));
        Json(coverage).Should().Be(Json(ComparisonSlices.Coverage(input, settings)));
    }

    [Fact]
    public void Slice_Should_GiveTheBaseOnlyTheTracersMinutesAndNoShadow_When_ItCuts()
    {
        var input = Campaign();

        var sliced = ComparisonSlices.Slice(input);

        sliced.Base.ShadowMinutes.Should().BeEmpty();
        sliced.Base.QueueMinutes.Select(r => (r.QueueZone, r.MinuteUtc.Minute, r.MinuteUtc.Hour)).Should().OnlyContain(k =>
            (k.QueueZone == Vis && ((k.Hour == 18 && k.Minute >= 8 && k.Minute <= 12) || (k.Hour == 18 && k.Minute >= 57) || (k.Hour == 19 && k.Minute <= 1))) ||
            (k.QueueZone == Cit && ((k.Hour == 18 && k.Minute >= 18 && k.Minute <= 22) || (k.Hour == 18 && k.Minute <= 2) || (k.Hour == 17 && k.Minute >= 58))));
        sliced.Base.QueueMinutes.Should().HaveCount(20, "four runs, five minutes each, all stored");
        sliced.Zones.Select(z => z.Scope.Zones.Single().Name).Should().Equal(Cit, Vis);
        sliced.Zones.Should().OnlyContain(z => z.Scope.Lines.Count == 0 && z.Scope.Desks.Count == 0 && z.QueueMinutes.All(r => r.QueueZone == z.Scope.Zones[0].Name));
        sliced.Zones.Sum(z => z.QueueMinutes.Count).Should().Be(input.QueueMinutes.Count);
        sliced.Zones.Sum(z => z.ShadowMinutes.Count).Should().Be(input.ShadowMinutes.Count);
    }

    [Fact]
    public void TracerMinutes_Should_ReachTwoMinutesEitherSideOfTheJoin_When_TheRunsZoneIsInScope()
    {
        var minutes = ComparisonSlices.TracerMinutes(Scope(), [Run(1, ZoneA, At(18, 10, 30), 8), Run(2, G(99), At(18, 30), 8), null,
            Run(3, ZoneB, new DateTime(2000, 1, 1, 0, 1, 0, DateTimeKind.Utc), 1), Run(4, ZoneB, DateTime.MaxValue.AddMinutes(-1), 0)]);

        minutes.Should().BeEquivalentTo(Enumerable.Range(8, 5).Select(m => (Vis, At(18, m))),
            "a zone out of scope, a null run and joins within two minutes of the engine's years add nothing");
    }

    [Fact]
    public void Pool_Should_CountRowsOfNoZoneInScopeAsTheWholeDoes_When_TheyAreGivenToTheFirstSlice()
    {
        var input = Campaign() with
        {
            QueueMinutes = [.. Campaign().QueueMinutes, Row("X-OTHER", At(18, 5), 1, 1), null, Row(Vis, At(18, 5, 30), 1, 1)],
            ShadowMinutes = [.. Campaign().ShadowMinutes, new ShadowMinuteRow("X-OTHER", At(18, 5), 1, null, false, null), null]
        };

        var whole = ValidationComparison.Compare(input);
        var (sliced, _) = ComparisonSlices.CompareSliced(input);

        whole.LeftOut.QueueMinutes.Should().Be(3);
        whole.LeftOut.ShadowMinutes.Should().Be(2);
        Json(sliced).Should().Be(Json(whole));
    }

    [Fact]
    public void Pool_Should_KeepUnusableMinuteKeysInTheWholesOrder_When_SeveralZonesHoldThem()
    {
        // A conflicting minute and a refused shadow in each zone: the keys come back by kind, zone and time, as the whole lists them.
        var input = Campaign() with
        {
            QueueMinutes = [.. Campaign().QueueMinutes, Row(Vis, At(18, 30), 99, 1), Row(Cit, At(18, 31), 98, 1)],
            ShadowMinutes = [.. Campaign().ShadowMinutes, new ShadowMinuteRow(Cit, At(18, 2), -1, null, false, null), new ShadowMinuteRow(Vis, At(18, 1), -1, null, false, null)]
        };

        var whole = ValidationComparison.Compare(input);
        var (sliced, _) = ComparisonSlices.CompareSliced(input);

        whole.LeftOut.UnusableKeys.Select(k => (k.Kind, k.QueueZone)).Should().Equal((UnusableKeyKind.QueueMinute, Cit), (UnusableKeyKind.QueueMinute, Vis),
            (UnusableKeyKind.ShadowMinute, Cit), (UnusableKeyKind.ShadowMinute, Vis));
        Json(sliced.LeftOut).Should().Be(Json(whole.LeftOut));
        sliced.LeftOut.Should().Be(whole.LeftOut);
    }

    [Fact]
    public void CompareSliced_Should_CompareEveryZone_When_TheWholeIsBeyondTheEnginesBoundOfQueueMinutes()
    {
        // 2 zones of 500,001 minutes each: 1,000,002 queue minutes, beyond the 1,000,000 of a kind the engine takes at once.
        var start = At(0, 0);
        var minutes = new List<QueueMinuteRow>(1_000_002);
        foreach (var zone in new[] { Vis, Cit })
        {
            for (var m = 0; m < 500_001; m++)
                minutes.Add(new QueueMinuteRow(zone, start.AddMinutes(m), Version, BinStatus.Final, 1, 5, 5, null, false));
        }

        var input = new ComparisonInput
        {
            Scope = Scope() with { Windows = [new UtcWindow(start, start.AddHours(1))] },
            QueueMinutes = minutes,
            QueueBins = [.. new[] { Vis, Cit }.SelectMany(z => Enumerable.Range(0, 8).Select(i => Bin(z, start.AddMinutes(15 * i))))]
        };

        ValidationComparison.Compare(input).Problem.Should().Be(ComparisonProblem.InputTooLarge);
        var (sliced, coverage) = ComparisonSlices.CompareSliced(input);

        sliced.Problem.Should().BeNull();
        sliced.NowcastZones.Select(z => (z.QueueZone, z.Published.Judged.Minutes)).Should().Equal((Cit, 60), (Vis, 60));
        sliced.NowcastOverall.Published.Judged.Minutes.Should().Be(120);
        coverage.Should().OnlyContain(c => c.PlannedMinutes == 60 && c.WithRealisedWait == 60 && c.Published == 60 && c.Share == 1.0);
    }

    [Fact]
    public void CompareSliced_Should_ReturnTheWholesProblem_When_TheScopeIsInvalidOrHasNoZone()
    {
        var invalid = Campaign() with { Scope = Scope() with { Windows = null } };
        var noZone = new ComparisonInput { Scope = new ComparisonScope(Version, [], [], [new UtcWindow(At(18, 0), At(19, 0))]), QueueMinutes = [Row(Vis, At(18, 0), 1, 1)] };

        Json(ComparisonSlices.CompareSliced(invalid).Result).Should().Be(Json(ValidationComparison.Compare(invalid)));
        ComparisonSlices.CompareSliced(invalid).Result.Problem.Should().Be(ComparisonProblem.InvalidScope);
        Json(ComparisonSlices.CompareSliced(noZone).Result).Should().Be(Json(ValidationComparison.Compare(noZone)));
        ComparisonSlices.Slice(noZone).Zones.Should().BeEmpty();
    }

    [Fact]
    public void Pool_Should_RefuseZoneResultsThatAreNotOneZoneOfTheBasesVersion_When_Given()
    {
        var input = Campaign();
        var sliced = ComparisonSlices.Slice(input);
        var baseResult = ValidationComparison.Compare(sliced.Base);
        var both = ValidationComparison.Compare(input);
        var zone = ComparisonSlices.CompareZone(sliced.Zones[0]).Result;

        var twoZones = () => ComparisonSlices.Pool(baseResult, [both]);
        var otherVersion = () => ComparisonSlices.Pool(baseResult, [zone with { ProfileVersion = Version + 1 }]);

        var other = ComparisonSlices.CompareZone(sliced.Zones[1]).Result;
        var twice = () => ComparisonSlices.Pool(baseResult, [zone, zone]);
        var missing = () => ComparisonSlices.Pool(baseResult, [zone]);
        var foreign = () => ComparisonSlices.Pool(baseResult, [zone, other with { NowcastZones = [other.NowcastZones[0] with { QueueZone = "X-OTHER" }] }]);

        twoZones.Should().Throw<ArgumentException>();
        otherVersion.Should().Throw<ArgumentException>();
        // L2 of the ARV-104g2 review: exactly one result per zone of the base's scope.
        twice.Should().Throw<ArgumentException>();
        missing.Should().Throw<ArgumentException>();
        foreign.Should().Throw<ArgumentException>();
        ComparisonSlices.Pool(baseResult, [other, zone]).Problem.Should().BeNull("any order of the zones is pooled");
        ComparisonSlices.Pool(baseResult, [zone with { Problem = ComparisonProblem.InputTooLarge }, other]).Problem.Should().Be(ComparisonProblem.InputTooLarge);
        ComparisonSlices.Pool(baseResult, []).Should().BeSameAs(baseResult);
    }

    [Fact]
    public void Coverage_Should_CountPlannedMinutesWithARealisedWaitAndThoseThePublishedNowcastCovered_When_Compared()
    {
        // 18:00 to 18:59 planned; 18:00 to 18:09 stored but 18:07, each with a realised wait. A next minute with a wait: 18:00 to
        // 18:05, 18:07 and 18:08 (8 minutes; 18:06's next is missing). Of those, 18:03 said NothingOpen, and 18:05 (no live part)
        // and 18:07 (no row) published nothing.
        var rows = Enumerable.Range(0, 10).Where(m => m != 7).Select(m => m switch
        {
            3 => Row(Vis, At(18, m), null, 5, "NothingOpen"),
            5 => Row(Vis, At(18, m), null, 5, live: false),
            _ => Row(Vis, At(18, m), 5, 5)
        }).ToList();
        var input = new ComparisonInput { Scope = Scope(), QueueMinutes = rows, QueueBins = [Bin(Vis, At(18, 0)), Bin(Vis, At(18, 15))] };

        var coverage = ComparisonSlices.Coverage(input).Single(c => c.QueueZone == Vis);

        coverage.Should().Be(new NowcastCoverage(Vis, 60, 8, 5, 1, 2, 5 / 8.0));
        NowcastCoverage.Sum(ComparisonSlices.Coverage(input)).Should().Be(new NowcastCoverage(null, 120, 8, 5, 1, 2, 5 / 8.0));
        NowcastCoverage.Sum([]).Share.Should().BeNull();
    }
}
