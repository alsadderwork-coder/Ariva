using Ariva.Core.Alerting;
using Ariva.Core.Border;
using Ariva.Core.Desks;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Flights;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using CsCheck;
using FluentAssertions;

namespace Ariva.UnitTests.Properties;

/// <summary>
/// ARV-070: invariants of the formulas of docs/domain/formulas.md that Ariva.Core implements, over generated inputs (the
/// fixed cases stay in each formula's own tests). F1 footprints, F4 geometry, F7 wait statistics, F8 nowcast and its
/// display, F9 and F20 predicted waits and alert rules, F12 e-gate coupling, F14 the arrival wave, F19 clock offsets, F22
/// the ring text the geometry hash is built from. A failure prints CsCheck's shrunk input and seed; the case goes into
/// the formula's own tests.
/// </summary>
public sealed class FormulaPropertyTests
{
    private static readonly int Iter = 5 * Fuzz.Iterations;
    private static readonly Gen<double> Wait = Gen.Double[0, 600];
    private static readonly Gen<double> Weight = Gen.Double[0.001, 50];
    private static readonly Gen<(double, double)[]> Samples = Gen.Select(Wait, Weight).Array[1, 60];
    private static readonly Gen<double> Share = Gen.Double[0.001, 1];

    // F1
    [Fact]
    public void F1_Footprint_Should_BePositive_AndNeverShrinkInLengthOrArea_AsTheSensorGoesHigher()
    {
        Gen.Select(Gen.Double[0.5, 30], Gen.Double[0, 5]).Sample((h, dh) =>
        {
            var low = CoverageFootprint.Assumed(DeviceFamily.StereoVision, h);
            var high = CoverageFootprint.Assumed(DeviceFamily.StereoVision, h + dh);
            low.LengthMetres.Should().BePositive();
            low.WidthMetres.Should().BePositive();
            high.LengthMetres.Should().BeGreaterThanOrEqualTo(low.LengthMetres!.Value - 0.01);
            // Length and width are given to 0.1 m: at a rounding edge either may step a whole 0.1 m, so the area may step back
            // by up to 0.1 x (length + width); the unrounded area never does.
            (high.LengthMetres!.Value * high.WidthMetres!.Value).Should().BeGreaterThanOrEqualTo(
                low.LengthMetres.Value * low.WidthMetres.Value - 0.1 * (low.LengthMetres.Value + low.WidthMetres.Value) - 0.01);
        }, iter: Iter);
    }

    // F4
    private static readonly Gen<FloorPoint> Point = Gen.Select(Gen.Double[-50, 50], Gen.Double[-50, 50]).Select((x, y) => new FloorPoint(x, y));

    /// <summary>
    /// ARV-161: the segment properties run at least 10,000 cases in every build (the order dependence at exact touches
    /// showed only on some seeds); a deep run (ARIVA_FUZZ_ITERATIONS) raises them with the others.
    /// </summary>
    private static readonly int SegmentIter = Math.Max(10_000, Iter);

    /// <summary>The seed that found the order dependence of ARV-161: two segments sharing an end at the origin.</summary>
    private const string SharedEndSeed = "cCcbgdLcNjA6";

    /// <summary>All eight ways to pass two segments (either first, each in either direction) give one answer.</summary>
    private static void SameInEveryOrder(FloorPoint p1, FloorPoint p2, FloorPoint q1, FloorPoint q2)
    {
        var crosses = Geometry.SegmentsIntersect(p1, p2, q1, q2);
        Geometry.SegmentsIntersect(p2, p1, q1, q2).Should().Be(crosses);
        Geometry.SegmentsIntersect(p1, p2, q2, q1).Should().Be(crosses);
        Geometry.SegmentsIntersect(p2, p1, q2, q1).Should().Be(crosses);
        Geometry.SegmentsIntersect(q1, q2, p1, p2).Should().Be(crosses);
        Geometry.SegmentsIntersect(q2, q1, p1, p2).Should().Be(crosses);
        Geometry.SegmentsIntersect(q1, q2, p2, p1).Should().Be(crosses);
        Geometry.SegmentsIntersect(q2, q1, p2, p1).Should().Be(crosses);
    }

    [Fact]
    public void F4_SegmentsIntersect_Should_NotDependOnOrderOrDirection()
    {
        Gen.Select(Point, Point, Point, Point).Sample(SameInEveryOrder, iter: SegmentIter);
    }

    /// <summary>The generator and property above, on the seed that failed before ARV-161 (the concrete points are a row of GeometryTests).</summary>
    [Fact]
    public void F4_SegmentsIntersect_Should_NotDependOnOrderOrDirection_When_TheSeedIsTheOneThatFoundARV161()
    {
        Gen.Select(Point, Point, Point, Point).Sample(SameInEveryOrder, seed: SharedEndSeed, iter: 1);
    }

    [Fact]
    public void F4_SegmentsIntersect_Should_FindATouch_When_AnEndIsPlacedOnTheOtherSegment()
    {
        Gen.Select(Point, Point, Gen.Double[0, 1], Point).Sample((p1, p2, t, q2) =>
        {
            var q1 = new FloorPoint(p1.X + (t * (p2.X - p1.X)), p1.Y + (t * (p2.Y - p1.Y)));
            SameInEveryOrder(p1, p2, q1, q2);
            Geometry.SegmentsIntersect(p1, p2, q1, q2).Should().BeTrue("an end on the other segment touches it, whatever the rounding");
        }, iter: SegmentIter);
    }

    [Fact]
    public void F4_SegmentsIntersect_Should_TouchWithinACentimetre_AndNotBeyond_When_ASegmentIsMovedSideways()
    {
        // The tolerance is a distance in metres whatever the segments' length: a cross product compared with it directly
        // would fail here for long or short segments.
        Gen.Select(Point, Point, Gen.Double[0, 0.0095], Gen.Double[0.0105, 1]).Sample((p1, p2, near, far) =>
        {
            var length = Geometry.Length(p1, p2);
            if (length < 0.05)
                return;
            var (nx, ny) = (-(p2.Y - p1.Y) / length, (p2.X - p1.X) / length);
            FloorPoint Moved(FloorPoint p, double by) => new(p.X + (by * nx), p.Y + (by * ny));
            Geometry.SegmentsIntersect(p1, p2, Moved(p1, near), Moved(p2, near)).Should().BeTrue("{0} m beside it is within a centimetre", near);
            Geometry.SegmentsIntersect(p1, p2, Moved(p1, -near), Moved(p2, -near)).Should().BeTrue("{0} m beside it is within a centimetre", near);
            Geometry.SegmentsIntersect(p1, p2, Moved(p1, far), Moved(p2, far)).Should().BeFalse("{0} m beside it is beyond a centimetre", far);
            Geometry.SegmentsIntersect(p1, p2, Moved(p1, -far), Moved(p2, -far)).Should().BeFalse("{0} m beside it is beyond a centimetre", far);
        }, iter: SegmentIter);
    }

    /// <summary>
    /// A point of a 10 cm grid by its integers (x 0.1 m, which binary cannot hold exactly): a small grid gives shared ends,
    /// ends on the other segment and collinear overlaps; a larger one ends within a centimetre of a long segment.
    /// </summary>
    private static readonly Gen<(int X, int Y)> GridPoint = Gen.Frequency(
        (1, Gen.Select(Gen.Int[-4, 4], Gen.Int[-4, 4])),
        (1, Gen.Select(Gen.Int[-40, 40], Gen.Int[-40, 40])));

    private static FloorPoint OnTheGrid((int X, int Y) g) => new(g.X * 0.1, g.Y * 0.1);

    private static long GridCross((int X, int Y) o, (int X, int Y) a, (int X, int Y) b) =>
        ((long)(a.X - o.X) * (b.Y - o.Y)) - ((long)(a.Y - o.Y) * (b.X - o.X));

    private static long GridDot((int X, int Y) o, (int X, int Y) a, (int X, int Y) b) =>
        ((long)(a.X - o.X) * (b.X - o.X)) + ((long)(a.Y - o.Y) * (b.Y - o.Y));

    /// <summary>Closer than a centimetre (0.1 grid unit) to the segment a-b, exactly.</summary>
    private static bool GridNear((int X, int Y) r, (int X, int Y) a, (int X, int Y) b)
    {
        var squared = GridDot(a, b, b);
        var along = GridDot(a, b, r);
        if (squared == 0 || along <= 0)
            return r == a;
        if (along >= squared)
            return r == b;
        var cross = GridCross(a, b, r);
        return 100 * cross * cross < squared;
    }

    private static bool GridWithin((int X, int Y) a, (int X, int Y) b, (int X, int Y) r) =>
        Math.Min(a.X, b.X) <= r.X && r.X <= Math.Max(a.X, b.X) && Math.Min(a.Y, b.Y) <= r.Y && r.Y <= Math.Max(a.Y, b.Y);

    /// <summary>
    /// The exact answer in integers: the segments meet, or an end is closer than a centimetre to the other segment. Null
    /// when an end is exactly a centimetre from the other's line, where either answer is right.
    /// </summary>
    private static bool? GridAnswer((int X, int Y) p1, (int X, int Y) p2, (int X, int Y) q1, (int X, int Y) q2)
    {
        (int X, int Y)[][] ends = [[p1, q1, q2], [p2, q1, q2], [q1, p1, p2], [q2, p1, p2]];
        if (ends.Any(e => GridDot(e[1], e[2], e[2]) > 0 && 100 * GridCross(e[1], e[2], e[0]) * GridCross(e[1], e[2], e[0]) == GridDot(e[1], e[2], e[2])))
            return null;
        var d1 = Math.Sign(GridCross(q1, q2, p1));
        var d2 = Math.Sign(GridCross(q1, q2, p2));
        var d3 = Math.Sign(GridCross(p1, p2, q1));
        var d4 = Math.Sign(GridCross(p1, p2, q2));
        var meet = (d1 * d2 < 0 && d3 * d4 < 0) ||
                   (d1 == 0 && GridWithin(q1, q2, p1)) || (d2 == 0 && GridWithin(q1, q2, p2)) ||
                   (d3 == 0 && GridWithin(p1, p2, q1)) || (d4 == 0 && GridWithin(p1, p2, q2));
        return meet || ends.Any(e => GridNear(e[0], e[1], e[2]));
    }

    [Fact]
    public void F4_SegmentsIntersect_Should_GiveTheExactAnswer_When_ThePointsAreOnADecimetreGrid()
    {
        Gen.Select(GridPoint, GridPoint, GridPoint, GridPoint).Sample((p1, p2, q1, q2) =>
        {
            if (GridAnswer(p1, p2, q1, q2) is not { } expected)
                return;
            SameInEveryOrder(OnTheGrid(p1), OnTheGrid(p2), OnTheGrid(q1), OnTheGrid(q2));
            Geometry.SegmentsIntersect(OnTheGrid(p1), OnTheGrid(p2), OnTheGrid(q1), OnTheGrid(q2)).Should().Be(expected,
                "integer arithmetic says so for {0} {1} and {2} {3} (decimetres)", p1, p2, q1, q2);
        }, iter: SegmentIter);
    }

    /// <summary>Rings of 3 to 7 corners on a small 10 cm grid, simple or not: corners on other edges and edges along each other are common.</summary>
    private static readonly Gen<FloorPoint[]> GridRing = Gen.Select(Gen.Int[0, 6], Gen.Int[0, 6]).Select((x, y) => new FloorPoint(x * 0.1, y * 0.1)).Array[3, 7];

    [Fact]
    public void F4_IsSimplePolygonAndOverlaps_Should_NotDependOnWhereARingStartsOrItsDirection()
    {
        Gen.Select(GridRing, GridRing, Gen.Int[0, 6]).Sample((ring, other, shift) =>
        {
            var k = shift % ring.Length;
            FloorPoint[] rotated = [.. ring.Skip(k), .. ring.Take(k)];
            var reversed = ring.Reverse().ToArray();
            var simple = Geometry.IsSimplePolygon(ring);
            Geometry.IsSimplePolygon(rotated).Should().Be(simple);
            Geometry.IsSimplePolygon(reversed).Should().Be(simple);
            if (!simple || !Geometry.IsSimplePolygon(other))
                return;
            var overlaps = Geometry.Overlaps(ring, other);
            Geometry.Overlaps(other, ring).Should().Be(overlaps);
            Geometry.Overlaps(rotated, other).Should().Be(overlaps);
            Geometry.Overlaps(reversed, other.Reverse().ToArray()).Should().Be(overlaps);
        }, iter: Iter);
    }

    /// <summary>A convex ring: points on a circle at increasing angles at least 0.05 rad apart (no corner twice).</summary>
    private static readonly Gen<FloorPoint[]> Convex = Gen.Select(Gen.Double[0.5, 20], Gen.Int[0, 120].Array[3, 12], Point).Select((radius, steps, centre) =>
        steps.Distinct().Order().Select(k => k * 0.05).Select(a => new FloorPoint(centre.X + radius * Math.Cos(a), centre.Y + radius * Math.Sin(a))).ToArray())
        .Where(r => r.Length >= 3 && Math.Abs(Geometry.SignedArea(r)) > 0.01);

    [Fact]
    public void F4_Rings_Should_KeepTheirAreaAndContents_When_RotatedOrReversed()
    {
        Gen.Select(Convex, Gen.Int[0, 11], Point).Sample((ring, shift, p) =>
        {
            var rotated = ring.Skip(shift % ring.Length).Concat(ring.Take(shift % ring.Length)).ToArray();
            var reversed = ring.Reverse().ToArray();
            Geometry.SignedArea(rotated).Should().BeApproximately(Geometry.SignedArea(ring), 1e-6);
            Geometry.SignedArea(reversed).Should().BeApproximately(-Geometry.SignedArea(ring), 1e-6);
            Geometry.IsSimplePolygon(ring).Should().BeTrue("a convex ring is simple");
            var inside = Geometry.ContainsPoint(ring, p);
            Geometry.ContainsPoint(rotated, p).Should().Be(inside);
            Geometry.ContainsPoint(reversed, p).Should().Be(inside);
            var centroid = new FloorPoint(ring.Average(q => q.X), ring.Average(q => q.Y));
            Geometry.ContainsPoint(ring, centroid).Should().BeTrue("a convex ring holds the mean of its corners");
        }, iter: Iter);
    }

    // F22: the hash is built from the ring text, which must be canonical.
    [Fact]
    public void F22_RingText_Should_BeCanonical_AfterOneRoundTrip()
    {
        Gen.Select(Gen.Double[-1e4, 1e4], Gen.Double[-1e4, 1e4]).Array[1, 40].Sample(xy =>
        {
            var text = Geometry.FormatRing(xy.Select(p => new FloorPoint(p.Item1, p.Item2)));
            var parsed = Geometry.ParseRing(text, 200);
            parsed.Should().NotBeNull();
            Geometry.FormatRing(parsed).Should().Be(text, "the text the geometry hash covers reads back to itself");
            parsed.Zip(xy).Should().OnlyContain(z => Math.Abs(z.First.X - z.Second.Item1) <= 0.0005 + 1e-9 && Math.Abs(z.First.Y - z.Second.Item2) <= 0.0005 + 1e-9);
        }, iter: Iter);
    }

    // F7
    [Fact]
    public void F7_Percentile_Should_BeAWaitOfTheBin_AndGrowWithP()
    {
        Gen.Select(Samples, Share, Share).Sample((samples, p, q) =>
        {
            var (lo, hi) = (Math.Min(p, q), Math.Max(p, q));
            var a = WaitStatistics.Percentile(samples, lo)!.Value;
            var b = WaitStatistics.Percentile(samples, hi)!.Value;
            samples.Select(s => s.Item1).Should().Contain(a, "a nearest-rank percentile is one of the waits");
            b.Should().BeGreaterThanOrEqualTo(a);
            a.Should().BeInRange(samples.Min(s => s.Item1), samples.Max(s => s.Item1));
            WaitStatistics.Percentile(samples, 1).Should().Be(samples.Max(s => s.Item1));
        }, iter: Iter);
    }

    [Fact]
    public void F7_Statistics_Should_NotChange_When_EveryWeightIsScaled()
    {
        Gen.Select(Samples, Share, Gen.OneOfConst(2.0, 4.0, 0.5, 8.0)).Sample((samples, p, k) =>
        {
            var scaled = samples.Select(s => (s.Item1, s.Item2 * k)).ToArray();
            WaitStatistics.Percentile(scaled, p).Should().Be(WaitStatistics.Percentile(samples, p), "every passenger counts once whatever the unit");
            WaitStatistics.Mean(scaled)!.Value.Should().BeApproximately(WaitStatistics.Mean(samples)!.Value, 1e-9 * 600);
        }, iter: Iter);
    }

    [Fact]
    public void F7_Share_Should_BeAFraction_ThatGrowsWithTheTarget_AndAgreesWithTheMean()
    {
        Gen.Select(Samples, Wait, Wait).Sample((samples, t, u) =>
        {
            var (lo, hi) = (Math.Min(t, u), Math.Max(t, u));
            var a = WaitStatistics.Share(samples, lo)!.Value;
            var b = WaitStatistics.Share(samples, hi)!.Value;
            a.Should().BeInRange(0, 1);
            b.Should().BeGreaterThanOrEqualTo(a);
            WaitStatistics.Share(samples, samples.Max(s => s.Item1))!.Value.Should().BeApproximately(1, 1e-12);
            WaitStatistics.Mean(samples)!.Value.Should().BeInRange(samples.Min(s => s.Item1) - 1e-9, samples.Max(s => s.Item1) + 1e-9);
        }, iter: Iter);
    }

    [Fact]
    public void F7_HistogramPercentile_Should_BeTheExactOne_ToTheBucket()
    {
        // A bin's waits, their histogram, and the merged histogram of the bin split in two: the bucketed percentile is the
        // upper edge of the exact one's bucket, and merging halves gives what the whole would.
        Gen.Select(Gen.Double[0, 300].Array[1, 80], Share, Gen.Int[0, 80]).Sample((waits, p, cut) =>
        {
            static IReadOnlyList<(int, long)> Histogram(IEnumerable<double> w) =>
                [.. w.GroupBy(x => WaitStatistics.BucketOf(TimeSpan.FromMinutes(x))).OrderBy(g => g.Key).Select(g => (g.Key, (long)g.Count()))];
            var exact = WaitStatistics.Percentile(waits, p)!.Value;
            var bucketed = WaitStatistics.Percentile(Histogram(waits), p)!.Value;
            var width = WaitStatistics.BucketWidth.TotalMinutes;
            bucketed.Should().BeGreaterThanOrEqualTo(exact - 1e-9).And.BeLessThanOrEqualTo(exact + width + 1e-9);
            var c = Math.Min(cut, waits.Length);
            WaitStatistics.Percentile(WaitStatistics.Merge(Histogram(waits[..c]), Histogram(waits[c..])), p).Should().Be(bucketed);
        }, iter: Iter);
    }

    // F8
    private static readonly Gen<NowcastInput> Inputs = Gen.Select(Gen.Int[0, 2_000], Gen.Int[1, 40], Gen.Double[0.05, 10], Gen.Long[0, 3_000], Gen.Double[0, 1],
        Gen.Double[0, 0.9], Gen.Bool).Select((q, open, cycle, exits, share, reject, desksKnown) => new NowcastInput
        {
            QueueLength = q,
            OpenServers = desksKnown ? open : null,
            CycleMinutes = desksKnown ? cycle : null,
            ExitsInWindow = exits,
            ExitWindowMinutes = 5,
            MergeShare = Math.Max(share, 0.05),
            RejectRate = reject
        });

    [Fact]
    public void F8_Nowcast_Should_BeFiniteAndPositive_AndGrowWithTheQueue()
    {
        Gen.Select(Inputs, Gen.Int[0, 500]).Sample((input, more) =>
        {
            var now = Nowcast.Compute(input);
            if (now.Minutes is not { } w)
            {
                now.NoService.Should().NotBeNull("no number comes with its reason");
                return;
            }

            double.IsFinite(w).Should().BeTrue();
            w.Should().BePositive();
            now.Throughput.Should().BePositive();
            w.Should().BeApproximately((input.QueueLength!.Value + 1.0) / now.Throughput!.Value, 1e-9 * w, "F8: W = (Q + 1) / mu");
            var longer = Nowcast.Compute(input with { QueueLength = input.QueueLength + more });
            longer.Minutes.Should().BeGreaterThanOrEqualTo(w, "a longer queue never waits less");
            var rejects = Nowcast.Compute(input with { RejectRate = Math.Min(0.95, input.RejectRate + 0.05) });
            if (rejects.Minutes is { } r)
                r.Should().BeGreaterThanOrEqualTo(w - 1e-9, "more rejects never shorten the wait");
        }, iter: Iter);
    }

    [Fact]
    public void F8_Display_Should_ShowABandHoldingTheWait_OrSayItIsBeyondTheCeiling()
    {
        Gen.Select(Gen.Double[0, 400], Gen.Bool).Sample((w, degraded) =>
        {
            var shown = NowcastDisplays.Next(NowcastDisplayState.Initial, new NowcastResult(w, 1, ThroughputSource.Desks, null, degraded), stale: false).Shown;
            switch (shown.Kind)
            {
                case NowcastDisplayKind.AboveCeiling:
                    w.Should().BeGreaterThanOrEqualTo(NowcastDisplays.Ceiling);
                    break;
                case NowcastDisplayKind.UnderFive:
                    w.Should().BeLessThan(5);
                    break;
                default:
                    shown.FromMinutes.Should().NotBeNull();
                    if (!degraded || w <= 0.75 * NowcastDisplays.Ceiling)
                        w.Should().BeGreaterThanOrEqualTo(shown.FromMinutes!.Value - 1e-9).And.BeLessThanOrEqualTo(shown.ToMinutes!.Value + 1e-9, "the band holds the estimate");
                    (shown.ToMinutes - shown.FromMinutes).Should().BeGreaterThanOrEqualTo(degraded ? 10 : 5);
                    if (!degraded)
                        (shown.ToMinutes - shown.FromMinutes).Should().Be(5, "a live band is 5 minutes wide");
                    break;
            }
        }, iter: Iter);
    }

    [Fact]
    public void F8_DegradedBand_Should_BeAtLeastTenMinutesWide_AndWithinTheCeiling()
    {
        Gen.Double[0, 1_000].Sample(w =>
        {
            var (from, to) = NowcastDisplays.DegradedBand(w);
            (to - from).Should().BeGreaterThanOrEqualTo(10);
            from.Should().BeGreaterThanOrEqualTo(0);
            to.Should().BeLessThanOrEqualTo(NowcastDisplays.Ceiling);
            (from % 5).Should().Be(0);
            (to % 5).Should().Be(0);
            if (w <= 0.75 * NowcastDisplays.Ceiling)
                w.Should().BeInRange(from, to, "below the ceiling the band holds the estimate");
        }, iter: Iter);
    }

    // F9 and F20
    [Fact]
    public void F9_PredictedPeak_Should_BeAtLeastTheFirstMinutesNowcast_AndGrowWithArrivals()
    {
        Gen.Select(Gen.Int[0, 500], Gen.Double[0.2, 30], Gen.Double[0, 40].Array[1, 30], Gen.Double[0, 20]).Sample((q, mu, arrivals, extra) =>
        {
            var lead = arrivals.Length;
            var peak = PredictedWait.Peak(q, mu, arrivals, lead);
            peak.Should().NotBeNull();
            peak!.AheadMinutes.Should().BeInRange(1, lead);
            peak.Minutes.Should().BeGreaterThanOrEqualTo(1 / mu - 1e-9, "never below an empty queue's wait");
            peak.Minutes.Should().BeGreaterThanOrEqualTo((Math.Max(0, q + arrivals[0] - mu) + 1) / mu - 1e-9);
            var more = arrivals.Select((a, i) => i == 0 ? a + extra : a).ToArray();
            PredictedWait.Peak(q, mu, more, lead)!.Minutes.Should().BeGreaterThanOrEqualTo(peak.Minutes - 1e-9, "more arrivals never lower the peak");
        }, iter: Iter);
    }

    private static readonly Gen<AlertRuleValues> Rules = Gen.Select(Gen.Enum<AlertComparator>(), Gen.Double[0, 60], Gen.Int[1, 5], Gen.Int[1, 5])
        .Select((comparator, threshold, sustain, clear) => new AlertRuleValues("rule", ["A-VIS"], AlertMetric.Nowcast, comparator, threshold, null, null, sustain, clear,
            AlertSeverity.Warning, "DutyManager", null, null, null, false, true));

    [Fact]
    public void F20_Alerts_Should_AlternateRaisedAndCleared_AndRunAsTheirSteps()
    {
        var start = new DateTime(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc);
        Gen.Select(Rules, Gen.Double[0, 60].Array[0, 120]).Sample((rule, values) =>
        {
            var minutes = values.Select((v, i) => new AlertMinute(start.AddMinutes(i), v)).ToList();
            var (state, transitions) = AlertEvaluator.Run(rule, AlertTargetState.Fresh, minutes);
            for (var i = 0; i < transitions.Count; i++)
                transitions[i].Kind.Should().Be(i % 2 == 0 ? AlertTransitionKind.Raised : AlertTransitionKind.Cleared, "an alert is raised, then cleared, then raised again");
            state.Armed.Should().Be(transitions.Count % 2 == 0);

            var stepped = AlertTargetState.Fresh;
            var count = 0;
            foreach (var minute in minutes)
            {
                (stepped, var t) = AlertEvaluator.Step(rule, stepped, minute);
                if (t is not null)
                    count++;
            }

            stepped.Should().Be(state);
            count.Should().Be(transitions.Count);
            AlertEvaluator.Run(rule, state, minutes).Transitions.Should().BeEmpty("minutes already seen change nothing");
        }, iter: Iter);
    }

    // F12
    private static readonly DateTime Minute0 = new(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc);

    private static readonly Gen<MinuteDemand[]> Demand = Gen.Select(Gen.Double[0, 30], Gen.Double[0, 30], Gen.Double[0, 30], Gen.Double[0, 5], Gen.Double[0, 30]).Array[1, 40]
        .Select(m => m.Select((c, i) => new MinuteDemand(Minute0.AddMinutes(i), new LaneCounts(c.Item1, c.Item2, c.Item3, c.Item4, c.Item5))).ToArray());

    [Fact]
    public void F12_Coupling_Should_AddTheRejectedEGateShareToTheRejectLane_AndNothingElse()
    {
        Gen.Select(Demand, Gen.Double[0, 1], Gen.Int[0, 10], Gen.OneOfConst("CIT", "RES", "VIS", "CRW")).Sample((minutes, rate, lag, lane) =>
        {
            var coupled = EgateCoupling.Couple(minutes, rate, lag, lane);
            coupled.Should().HaveCount(minutes.Length);
            var added = 0.0;
            for (var i = 0; i < minutes.Length; i++)
            {
                var delta = coupled[i].Lanes.Of(lane) - minutes[i].Lanes.Of(lane);
                delta.Should().BeGreaterThanOrEqualTo(-1e-9);
                delta.Should().BeApproximately(i >= lag ? rate * minutes[i - lag].Lanes.EGate : 0, 1e-9);
                foreach (var other in new[] { "CIT", "RES", "VIS", "CRW", "EG" }.Where(l => l != lane))
                    coupled[i].Lanes.Of(other).Should().Be(minutes[i].Lanes.Of(other));
                added += delta;
            }

            added.Should().BeApproximately(rate * minutes.Take(Math.Max(0, minutes.Length - lag)).Sum(m => m.Lanes.EGate), 1e-6, "every reject is demand once, lag minutes later");
        }, iter: Iter);
    }

    [Fact]
    public void F12_RejectRate_Should_BeAShare_AndFallBackBelowTheMinimumAttempts()
    {
        var settings = new EgateCouplingSettings();
        Gen.Select(Gen.Long[0, 10_000], Gen.Long[-5, 10_000]).Sample((attempts, rejected) =>
        {
            var (rate, measured) = EgateCoupling.RejectRate(attempts, rejected, settings);
            rate.Should().BeInRange(0, 1);
            measured.Should().Be(attempts >= settings.MinAttempts && rejected >= 0 && rejected <= attempts);
            if (!measured)
                rate.Should().Be(settings.ReferenceRejectRate);
        }, iter: Iter);
    }

    // F14
    [Fact]
    public void F14_LaneSplit_Should_KeepEveryNonTransferPassenger()
    {
        Gen.Select(Gen.Double[0, 600], Gen.Double[0, 1]).Sample((passengers, egate) =>
        {
            var mix = LaneMix.Reference with { EGateShare = egate };
            var split = mix.Split(passengers);
            split.Total.Should().BeApproximately(passengers * (1 - mix.Trf), 1e-9 * Math.Max(1, passengers), "transfers leave; everyone else lands in a lane");
            new[] { split.Cit, split.Res, split.Vis, split.Crw, split.EGate }.Should().OnlyContain(x => x >= 0);
        }, iter: Iter);
    }

    [Fact]
    public void F14_Projection_Should_SpreadEachFlightsPassengersOverTheHall_WithoutLosingOrAddingAny()
    {
        var now = new DateTime(2026, 10, 3, 18, 0, 30, DateTimeKind.Utc);
        var flight = Gen.Select(Gen.Int[0, 29], Gen.Int[0, 400], Gen.Int[100, 999]).Select((minutes, pax, number) =>
            new ArrivingFlight($"RJ{number}-20261003-A", "RJ", number.ToString(System.Globalization.CultureInfo.InvariantCulture), null, "AMM", "T1", null,
                now.AddMinutes(minutes + 1), null, null, null, null, pax, null));
        flight.Array[0, 12].Sample(flights =>
        {
            var projection = ArrivalWave.Project(flights, now, 30, ArrivalWaveSettings.Default);
            projection.Minutes.Should().OnlyContain(m => m.Lanes.Total >= 0);
            var expected = projection.Flights.Where(f => f.Passengers is not null).Sum(f => f.Lanes.Total);
            projection.Minutes.Sum(m => m.Lanes.Total).Should().BeApproximately(expected, 1e-6 * Math.Max(1, expected), "the hall curve holds every passenger once");
            projection.Flights.All(f => f.HallFirstUtc >= now.AddMinutes(-1)).Should().BeTrue("every flight here lands after now");
        }, iter: Iter / 4);
    }

    // F19
    [Fact]
    public void F19_ClockOffset_Should_StayWithinItsReadings_AndKeepTenAtMost()
    {
        var received = new DateTime(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc);
        Gen.Double[-3_000, 3_000].Array[1, 30].Sample(offsets =>
        {
            var clock = ClockOffset.None;
            foreach (var ms in offsets)
                clock = clock.Next(received.AddMilliseconds(ms), received);
            clock.Recent.Count.Should().Be(Math.Min(ClockOffset.Window, offsets.Length));
            // Times are kept to the tick (0.0001 ms).
            clock.EstimateMilliseconds.Should().BeInRange(offsets.Min() - 0.001, offsets.Max() + 0.001, "an average of the readings");
            clock.Next(received.AddDays(2), received).Should().Be(clock, "a reading beyond a day moves nothing");
        }, iter: Iter);
    }

    [Fact]
    public void F19_ASteadyOffset_Should_BeCorrectedExactly()
    {
        var received = new DateTime(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc);
        Gen.Select(Gen.Double[-60_000, 60_000], Gen.Int[3, 20]).Sample((offset, readings) =>
        {
            var clock = ClockOffset.None;
            for (var i = 0; i < readings; i++)
                clock = clock.Next(received.AddMilliseconds(offset), received);
            clock.Stable.Should().BeTrue();
            if (Math.Abs(offset) > ClockOffset.ToleranceMilliseconds)
            {
                clock.State.Should().Be(ClockState.Corrected);
                (clock.Correct(received.AddMilliseconds(offset)) - received).TotalMilliseconds.Should().BeApproximately(0, 0.001);
            }
            else
            {
                clock.State.Should().Be(ClockState.Ok);
            }
        }, iter: Iter);
    }

    // F10
    private static readonly DateTime DeskStart = new(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc);

    private static readonly Gen<DeskSignal> Signal = Gen.Select(Gen.Int[0, 5], Gen.Int[0, 7_200], Gen.Int[0, 3], Gen.Int[0, 3]).Select((kind, second, a, b) =>
    {
        var at = DeskStart.AddSeconds(second);
        return kind switch
        {
            0 => (DeskSignal)new DeskTransactionStarted("IN01", at),
            1 => new DeskTransactionEnded("IN01", at),
            2 => new DeskTransactionsCompleted("IN01", at, a),
            3 => new DeskSessionChangedSignal("IN01", at, (DeskSessionSignal)(a % 3)),
            4 => new DeskZoneReading("IN01", at, b % 2 == 0 ? DeskSource.StaffZone : DeskSource.ServiceZone, a),
            _ => new DeskHeartbeat("IN01", at, (DeskSource)(b % 4))
        };
    });

    [Fact]
    public void F10_DeskState_Should_AccountForEverySecond_AndChainItsTransitions()
    {
        var profile = new DeskProfile("IN01", "CIT", HasTransactions: true, HasSession: true, HasStaffZone: true, HasServiceZone: true);
        Signal.Array[0, 80].Sample(signals =>
        {
            var engine = new DeskStateEngine([profile], DeskStart);
            var transitions = new List<DeskTransition>();
            var minutes = new List<DeskMinute>();
            void Drain(DateTime reference)
            {
                DeskStep step;
                var guard = 0;
                do
                {
                    step = engine.Advance(reference);
                    transitions.AddRange(step.Transitions);
                    minutes.AddRange(step.Minutes);
                }
                while (step.More && ++guard < 10_000);
            }

            foreach (var signal in signals.OrderBy(x => x.TimeUtc))
            {
                engine.Offer(signal, signal.TimeUtc);
                Drain(signal.TimeUtc);
            }

            Drain(DeskStart.AddHours(3));
            minutes.Should().NotBeEmpty();
            foreach (var m in minutes)
                (m.Closed + m.Idle + m.Serving + m.Paused + m.Unknown).Should().Be(TimeSpan.FromMinutes(1), "every second of {0:HH:mm} is in one state", m.MinuteUtc);
            minutes.Select(m => m.MinuteUtc).Should().OnlyHaveUniqueItems().And.BeInAscendingOrder();
            for (var i = 1; i < transitions.Count; i++)
            {
                transitions[i].From.Should().Be(transitions[i - 1].To, "a desk leaves the state it entered");
                transitions[i].AtUtc.Should().BeOnOrAfter(transitions[i - 1].AtUtc);
            }

            transitions.All(t => t.From != t.To).Should().BeTrue("a transition changes the state");
            var lane = engine.Lane("CIT");
            (lane.Serving + lane.Idle).Should().Be(lane.Open);
            (lane.Open + lane.Paused + lane.Closed + lane.Unknown).Should().Be(1, "one desk, in one state");
        }, iter: Iter / 4);
    }
}
