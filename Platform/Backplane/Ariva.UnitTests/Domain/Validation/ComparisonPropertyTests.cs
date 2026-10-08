using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using Ariva.Core.Validation.Comparison;
using Ariva.UnitTests.Properties;
using CsCheck;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Validation;

/// <summary>
/// ARV-104e: invariants of the F18 comparison over generated inputs (CsCheck; a failure prints the shrunk input or the seed of
/// the generated campaign, and the case joins <see cref="ComparisonTests"/>). Count accuracy stays within 0 and 1 and never
/// rises as the error grows; the tolerance is at least a minute and 10 percent and "within" is monotone in the error; the
/// bias's sign follows the errors, in the formula and in every zone the engine summarises; the sensitivity to an offset shift
/// is symmetric when the realised-wait curve is linear; track completion is a share; the result of a whole generated
/// campaign does not depend on the order of any input list, and its left-out counts stay between 0 and the rows given; and a
/// refused highest revision never lets an earlier one stand in (security review, CWE-501).
/// </summary>
public sealed class ComparisonPropertyTests
{
    private static readonly int Iter = 5 * Fuzz.Iterations;

    [Fact]
    public void F18_Accuracy_Should_StayWithinZeroAndOneAndNeverRise_When_TheErrorGrows()
    {
        Gen.Select(Gen.Double[0, 20_000], Gen.Double[0, 20_000], Gen.Double[0.5, 10_000]).Sample((s1, s2, manual) =>
        {
            var a1 = CountAccuracy.Accuracy(s1, manual).GetValueOrDefault(double.NaN);
            var a2 = CountAccuracy.Accuracy(s2, manual).GetValueOrDefault(double.NaN);
            a1.Should().BeInRange(0, 1);
            a2.Should().BeInRange(0, 1);
            if (Math.Abs(s1 - manual) <= Math.Abs(s2 - manual))
                a1.Should().BeGreaterThanOrEqualTo(a2 - 1e-12);
            CountAccuracy.Accuracy(s1, 0).Should().BeNull();
        }, iter: Iter);
    }

    [Fact]
    public void F18_Tolerance_Should_BeAtLeastAMinuteAndTenPercent_AndWithinShouldBeMonotone()
    {
        Gen.Select(Gen.Double[0, 240], Gen.Double[-60, 60], Gen.Double[-60, 60]).Sample((tracer, e1, e2) =>
        {
            var tolerance = TracerWaits.Tolerance(tracer).GetValueOrDefault();
            tolerance.Should().BeGreaterThanOrEqualTo(1).And.BeGreaterThanOrEqualTo(0.1 * tracer);
            (tolerance == 1 || Math.Abs(tolerance - (0.1 * tracer)) < 1e-12).Should().BeTrue("the tolerance is one of its two terms");
            if (Math.Abs(e1) <= Math.Abs(e2) && TracerWaits.IsWithin(e2, tracer) == true)
                TracerWaits.IsWithin(e1, tracer).Should().BeTrue();
        }, iter: Iter);
    }

    [Fact]
    public void F18_Bias_Should_FollowTheSignOfTheErrors_When_TracersAreCompared()
    {
        Gen.Select(Gen.Double[0.1, 180], Gen.Double[0, 240]).Array[1, 30].Sample(pairs =>
        {
            // Each pair is (w_tracer, w_system).
            var bias = TracerWaits.Bias(pairs.Select(p => (p.Item2, p.Item1))).GetValueOrDefault(double.NaN);
            var errors = pairs.Sum(p => p.Item2 - p.Item1);
            if (Math.Abs(errors) > 1e-9)
                Math.Sign(bias).Should().Be(Math.Sign(errors));
            if (pairs.All(p => p.Item2 >= p.Item1))
                bias.Should().BeGreaterThanOrEqualTo(0);
            if (pairs.All(p => p.Item2 <= p.Item1))
                bias.Should().BeLessThanOrEqualTo(0);
            bias.Should().BeGreaterThanOrEqualTo(-1, "a system wait is never below 0");
        }, iter: Iter);
    }

    [Fact]
    public void F18_TrackCompletionRate_Should_BeAShare_When_TracksEnteredAndExited()
    {
        Gen.Select(Gen.Long[1, 1_000_000], Gen.Double[0, 1]).Sample((entered, share) =>
        {
            var exited = (long)Math.Floor(entered * share);
            TrackCompletion.Rate(entered, exited).GetValueOrDefault(double.NaN).Should().BeInRange(0, 1);
        }, iter: Iter);
    }

    [Fact]
    public void F18_Sensitivity_Should_BeSymmetric_When_TheRealisedWaitCurveIsLinear()
    {
        // Minute 18:k holds the mean wait a + b x k (k = 0 to 60), so the system's wait is linear in the join time: shifting every
        // offset by +s and -s moves the error by the same amount in opposite directions. Joins stay inside 18:10 to 18:50, so a
        // shift of up to a minute never reaches the curve's ends.
        Gen.Select(Gen.Double[10, 60], Gen.Double[-0.15, 1.5], Gen.Int[600, 3_000], Gen.Int[1, 60_000], Gen.Int[60, 3_600]).Sample((a, b, joinSecond, shiftMs, waitSeconds) =>
        {
            var start = new DateTime(2026, 10, 8, 18, 0, 0, DateTimeKind.Utc);
            var joined = start.AddSeconds(joinSecond);
            var run = new TracerRunRow(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), ZoneId, "T-01", joined, joined.AddSeconds(waitSeconds), 0,
                joined, joined.AddSeconds(waitSeconds), false);
            var result = ValidationComparison.Compare(new ComparisonInput
            {
                Scope = Scope(start),
                TracerRuns = [run],
                QueueMinutes = [.. Enumerable.Range(0, 61).Select(k => new QueueMinuteRow(Zone, start.AddMinutes(k), 7, BinStatus.Final, 10, a + (b * k)))],
                QueueBins = [.. Enumerable.Range(0, 8).Select(i => new QueueBinRow(Zone, start.AddMinutes(15 * i), TimeSpan.FromMinutes(15), 1, BinStatus.Final, BinQuality.Good, 7, 10, 0))]
            }, new ComparisonSettings { SensitivityShift = TimeSpan.FromMilliseconds(shiftMs) });

            var tracer = result.Tracers.Single();
            tracer.Standing.Should().Be(ComparisonStanding.Good);
            var error = tracer.ErrorMinutes.GetValueOrDefault(double.NaN);
            var minus = tracer.ErrorOffsetMinusMinutes.GetValueOrDefault(double.NaN) - error;
            var plus = tracer.ErrorOffsetPlusMinutes.GetValueOrDefault(double.NaN) - error;
            (minus + plus).Should().BeApproximately(0, 1e-9);
            minus.Should().BeApproximately(b * shiftMs / 60_000.0, 1e-9, "lower offsets put the join later, where the curve has risen by b a minute");
            var sensitivity = result.TracerOverall.Sensitivity;
            sensitivity.Runs.Should().Be(1);
            (sensitivity.BiasOffsetMinus.GetValueOrDefault(double.NaN) - sensitivity.Bias.GetValueOrDefault(double.NaN) +
             (sensitivity.BiasOffsetPlus.GetValueOrDefault(double.NaN) - sensitivity.Bias.GetValueOrDefault(double.NaN))).Should().BeApproximately(0, 1e-9);
        }, iter: Iter);
    }

    [Fact]
    public void F18_DeskAgreement_Should_StayWithinZeroAndOneAndRise_When_AMinuteMoreAgrees()
    {
        // ARV-104f: agreeing over judged minutes is a share, and one more agreeing minute of the same judged ones never lowers it.
        Gen.Select(Gen.Int[1, 100_000], Gen.Double[0, 1]).Sample((judged, share) =>
        {
            var agreeing = (int)Math.Floor(judged * share);
            var agreement = DeskStateAgreement.Agreement(agreeing, judged).GetValueOrDefault(double.NaN);
            agreement.Should().BeInRange(0, 1);
            if (agreeing < judged)
                DeskStateAgreement.Agreement(agreeing + 1, judged).GetValueOrDefault(double.NaN).Should().BeGreaterThan(agreement);
            DeskStateAgreement.Agreement(agreeing, 0).Should().BeNull();
        }, iter: Iter);
    }

    [Fact]
    public void F18_Dominant_Should_BeAStateWithTheMostSecondsAndUnknownOnEveryTie_When_AMinuteIsSplit()
    {
        // ARV-104f: whatever the split of a minute (unaccounted seconds counting as Unknown), the dominant state holds at least as
        // many seconds as any other, and when Unknown holds as many as the most, the dominant state is Unknown.
        Gen.Select(Gen.Double[0, 1].Array[5], Gen.Double[0, 1]).Sample((weights, filled) =>
        {
            var total = weights.Sum();
            var seconds = weights.Select(w => total > 0 ? Math.Floor(w / total * 60 * filled * 1000) / 1000 : 0).ToArray();
            var dominant = DeskStateAgreement.Dominant(seconds[0], seconds[1], seconds[2], seconds[3], seconds[4]);
            dominant.Should().NotBeNull();
            var unknown = seconds[4] + Math.Max(0, 60 - seconds.Sum());
            var byStatus = new Dictionary<Ariva.Core.Desks.DeskStatus, double>
            {
                [Ariva.Core.Desks.DeskStatus.Closed] = seconds[0], [Ariva.Core.Desks.DeskStatus.Idle] = seconds[1], [Ariva.Core.Desks.DeskStatus.Serving] = seconds[2],
                [Ariva.Core.Desks.DeskStatus.Paused] = seconds[3], [Ariva.Core.Desks.DeskStatus.Unknown] = unknown
            };
            var most = byStatus.Values.Max();
            byStatus[dominant.GetValueOrDefault()].Should().BeGreaterThanOrEqualTo(most - 1e-9);
            if (unknown >= most - 1e-9)
                dominant.Should().Be(Ariva.Core.Desks.DeskStatus.Unknown);
        }, iter: Iter);
    }

    [Fact]
    public void F18_NowcastMedian_Should_NeverBeNegativeStayWithinTheErrorsAndNeverFall_When_AnErrorGrows()
    {
        // ARV-104f: the median absolute error is never negative, lies within the smallest and largest absolute error, and raising
        // one error's size never lowers it (the criterion is monotone in every error).
        Gen.Select(Gen.Double[-240, 240].Array[1, 60], Gen.Int[0, 59], Gen.Double[0, 30]).Sample((errors, at, more) =>
        {
            var absolute = errors.Select(Math.Abs).ToArray();
            var median = NowcastErrors.Median(absolute).GetValueOrDefault(double.NaN);
            median.Should().BeGreaterThanOrEqualTo(0).And.BeInRange(absolute.Min(), absolute.Max());
            var stats = NowcastErrors.Stats(errors);
            stats.MedianAbsoluteErrorMinutes.GetValueOrDefault(double.NaN).Should().Be(median);
            stats.MeanAbsoluteErrorMinutes.GetValueOrDefault(double.NaN).Should().BeGreaterThanOrEqualTo(Math.Abs(stats.MeanErrorMinutes.GetValueOrDefault()) - 1e-9);
            var raised = absolute.ToArray();
            raised[at % raised.Length] += more;
            NowcastErrors.Median(raised).GetValueOrDefault(double.NaN).Should().BeGreaterThanOrEqualTo(median);
        }, iter: Iter);
    }

    [Fact]
    public void F18_Comparison_Should_NotDependOnTheOrderOfItsInputs_And_KeepItsBoundsAndSigns()
    {
        // The campaign and the shuffles are drawn from values CsCheck generates (System.Random is refused by CA5394), so a
        // failure prints the values that reproduce it.
        Gen.UInt.Array[1_024].Sample(values =>
        {
            var draws = new Draws(values);
            var input = Campaign(draws);
            var shuffled = input with
            {
                ManualCounts = draws.Shuffled(input.ManualCounts),
                TracerRuns = draws.Shuffled(input.TracerRuns),
                LineBins = draws.Shuffled(input.LineBins),
                QueueMinutes = draws.Shuffled(input.QueueMinutes),
                QueueBins = draws.Shuffled(input.QueueBins),
                HealthBins = draws.Shuffled(input.HealthBins),
                QualityIntervals = draws.Shuffled(input.QualityIntervals),
                DeskObservations = draws.Shuffled(input.DeskObservations),
                DeskMinutes = draws.Shuffled(input.DeskMinutes),
                ShadowMinutes = draws.Shuffled(input.ShadowMinutes),
                Scope = input.Scope with { Zones = draws.Shuffled(input.Scope.Zones), Lines = draws.Shuffled(input.Scope.Lines), Desks = draws.Shuffled(input.Scope.Desks) }
            };

            var result = ValidationComparison.Compare(input);
            JsonSerializer.Serialize(ValidationComparison.Compare(shuffled)).Should().Be(JsonSerializer.Serialize(result));

            foreach (var share in result.CountBins.Select(i => i.Accuracy).Concat(result.Lines.Select(l => l.PooledAccuracy))
                         .Concat(result.TrackCompletion.Select(z => z.Good.Rate))
                         .Concat(result.Desks.Append(result.DeskOverall).SelectMany(d => new[] { d.Check.Value, d.ThroughputAgreement }))
                         .Where(v => v is not null))
                share.GetValueOrDefault().Should().BeInRange(0, 1);

            // ARV-104f: the desk tallies add up, Unknown never agrees, and every nowcast median is a size (never negative).
            foreach (var desk in result.Desks.Append(result.DeskOverall))
            {
                desk.Judged.Should().Be(desk.Minutes - desk.ObserversDisagree);
                desk.Excluded.Should().Be(desk.ObserversDisagree + desk.UnusableObservations);
                desk.Standings.Total.Should().Be(desk.Judged);
                desk.Confusion.Sum(c => c.Minutes).Should().Be(desk.Judged);
                desk.Agreeing.Should().BeLessThanOrEqualTo(desk.Judged - desk.Standings.Unknown);
            }

            result.DeskMinutes.Where(m => m.SystemState == Ariva.Core.Desks.DeskStatus.Unknown).Should().OnlyContain(m => m.Agrees != true);
            foreach (var zone in result.NowcastZones.Append(result.NowcastOverall))
            {
                foreach (var summary in new[] { zone.Published, zone.Shadow })
                {
                    summary.Excluded.Should().Be(summary.Minutes - summary.Judged.Minutes - summary.AtOrAboveCut.Minutes);
                    summary.Standings.Total.Should().Be(summary.Minutes);
                    foreach (var stats in new[] { summary.Judged, summary.AtOrAboveCut, summary.Flagged, summary.Degraded })
                        stats.MedianAbsoluteErrorMinutes.GetValueOrDefault().Should().BeGreaterThanOrEqualTo(0);
                }

                zone.Both.Minutes.Should().BeLessThanOrEqualTo(Math.Min(zone.Published.Judged.Minutes, zone.Shadow.Judged.Minutes));
            }
            foreach (var zone in result.TracerZones.Append(result.TracerOverall))
            {
                var compared = result.Tracers.Where(t => (zone.QueueZone is null || t.QueueZone == zone.QueueZone) && !t.Abandoned && t.Standing == ComparisonStanding.Good).ToList();
                zone.Compared.Should().Be(compared.Count);
                var errors = compared.Sum(t => t.ErrorMinutes.GetValueOrDefault());
                if (Math.Abs(errors) > 1e-9)
                    Math.Sign(zone.Bias.GetValueOrDefault()).Should().Be(Math.Sign(errors));
                zone.Standings.Total.Should().Be(result.Tracers.Count(t => (zone.QueueZone is null || t.QueueZone == zone.QueueZone) && !t.Abandoned));
                zone.Excluded.Should().Be(zone.Standings.Total - zone.Compared);
            }

            var left = result.LeftOut;
            left.ManualCounts.Should().BeInRange(0, input.ManualCounts.Count);
            left.TracerRuns.Should().BeInRange(0, input.TracerRuns.Count);
            left.LineBins.Should().BeInRange(0, input.LineBins.Count);
            left.QueueMinutes.Should().BeInRange(0, input.QueueMinutes.Count);
            left.QueueBins.Should().BeInRange(0, input.QueueBins.Count);
            left.HealthBins.Should().BeInRange(0, input.HealthBins.Count);
            left.QualityIntervals.Should().BeInRange(0, input.QualityIntervals.Count);
            left.DeskObservations.Should().BeInRange(0, input.DeskObservations.Count);
            left.DeskMinutes.Should().BeInRange(0, input.DeskMinutes.Count);
            left.ShadowMinutes.Should().BeInRange(0, input.ShadowMinutes.Count);
            left.UnusableKeys.Should().OnlyHaveUniqueItems();
        }, iter: Fuzz.Iterations);
    }

    [Fact]
    public void F18_Comparison_Should_NeverLetAnEarlierRevisionStandIn_When_TheLatestIsRefused()
    {
        // Whatever a generated campaign holds, a refused highest revision of one zone's queue bin and health bin, and of one
        // observer's count of a line and bin, leaves nothing Good over that bin and that line and bin unjudged.
        Gen.UInt.Array[1_024].Sample(values =>
        {
            var draws = new Draws(values);
            var input = Campaign(draws);
            var zone = draws.Next(2) == 0 ? Zone : "A-CIT";
            var bin = new DateTime(2026, 10, 8, 18, 0, 0, DateTimeKind.Utc).AddMinutes(15 * draws.Next(4));
            var line = input.Scope.Lines[draws.Next(input.Scope.Lines.Count)];
            var observer = Guid.Parse("00000000-0000-7000-8000-000000000901");
            var desk = input.Scope.Desks[draws.Next(input.Scope.Desks.Count)];
            var minute = bin.AddMinutes(draws.Next(15));
            var result = ValidationComparison.Compare(input with
            {
                QueueBins = [.. input.QueueBins, new QueueBinRow(zone, bin, TimeSpan.FromMinutes(15), 9, BinStatus.Final, BinQuality.Good, 7, 10, 11)],
                HealthBins =
                [
                    .. input.HealthBins, new ZoneHealthBin(zone, bin, TimeSpan.FromMinutes(15), 9, BinStatus.Final, 7, 0, 0, null, null, null, 10, 11, 0, 0, 0, 0, 0, null,
                        0, 0, 0)
                ],
                ManualCounts = [.. input.ManualCounts, new ManualCountRow(line.LineId, bin, observer, 9, -1, 0)],
                // ARV-104f: a refused highest revision of an observer's desk state leaves that desk minute unjudged.
                DeskObservations = [.. input.DeskObservations, new DeskObservationRow(desk.DeskId, minute, observer, 9, (ObservedDeskState)9)]
            });

            result.LeftOut.UnusableKeys.Should().Contain(new UnusableKey(UnusableKeyKind.QueueBin, zone, null, bin, null, UnusableKeyReason.Refused))
                .And.Contain(new UnusableKey(UnusableKeyKind.HealthBin, zone, null, bin, null, UnusableKeyReason.Refused))
                .And.Contain(new UnusableKey(UnusableKeyKind.ManualCount, line.QueueZone, line.Name, bin, observer, UnusableKeyReason.Refused))
                .And.Contain(new UnusableKey(UnusableKeyKind.DeskObservation, null, null, minute, observer, UnusableKeyReason.Refused, desk.DeskId));
            result.DeskMinutes.Should().NotContain(m => m.DeskId == desk.DeskId && m.MinuteUtc == minute);
            result.NowcastMinutes.Should().NotContain(m => m.QueueZone == zone && m.MinuteUtc >= bin && m.MinuteUtc < bin.AddMinutes(15) && m.Standing == ComparisonStanding.Good);
            result.CountBins.Should().NotContain(i => i.LineId == line.LineId && i.BinStartUtc == bin);
            result.CountBins.Should().NotContain(i => i.QueueZone == zone && i.BinStartUtc == bin && i.Standing == ComparisonStanding.Good);
            result.Tracers.Should().NotContain(t => t.QueueZone == zone && t.JoinedUtc < bin.AddMinutes(15) && t.ExitedUtc > bin && t.Standing == ComparisonStanding.Good);
            result.TrackCompletion.Single(z => z.QueueZone == zone).Bins.Unknown.Should().BeGreaterThanOrEqualTo(1);
        }, iter: Fuzz.Iterations);
    }

    #region Generated campaign

    private const string Zone = "A-VIS";
    private static readonly Guid ZoneId = Guid.Parse("00000000-0000-7000-8000-000000000001");

    private static ComparisonScope Scope(DateTime start) =>
        new(7, [new ScopeZone(ZoneId, Zone)], [new ScopeLine(Guid.Parse("00000000-0000-7000-8000-000000000011"), "A-VIS entry", LineRole.Entry, Zone)],
            [new UtcWindow(start, start.AddHours(2))]);

    /// <summary>Draws from generated values in turn (round the array when it runs out): numbers below a bound, and shuffles.</summary>
    private sealed class Draws(uint[] values)
    {
        private int _next;

        /// <summary>A number from 0 to below <paramref name="max"/>.</summary>
        public int Next(int max) => (int)(values[_next++ % values.Length] % (uint)max);

        /// <summary>A number from <paramref name="min"/> to below <paramref name="max"/>.</summary>
        public int Next(int min, int max) => min + Next(max - min);

        public double NextDouble() => values[_next++ % values.Length] / (double)uint.MaxValue;

        /// <summary>A Fisher-Yates shuffle of a copy.</summary>
        public IReadOnlyList<T> Shuffled<T>(IReadOnlyList<T> list)
        {
            var copy = list.ToArray();
            for (var i = copy.Length - 1; i > 0; i--)
            {
                var j = Next(i + 1);
                (copy[i], copy[j]) = (copy[j], copy[i]);
            }

            return copy;
        }
    }

    /// <summary>
    /// A campaign of two zones and five lines over an hour, every kind of input drawn at random: counts of two observers with
    /// corrections, line counts (some under another version, some a vendor's), stored bins of every quality, status and two
    /// revisions, minutes, tracer runs in four batches (some abandoned), zone health and outages, and a few unusable rows.
    /// </summary>
    private static ComparisonInput Campaign(Draws random)
    {
        var start = new DateTime(2026, 10, 8, 18, 0, 0, DateTimeKind.Utc);
        var zones = new[] { new ScopeZone(ZoneId, Zone), new ScopeZone(Guid.Parse("00000000-0000-7000-8000-000000000002"), "A-CIT") };
        var lines = new[]
        {
            new ScopeLine(Guid.Parse("00000000-0000-7000-8000-000000000011"), "A-VIS entry", LineRole.Entry, Zone),
            new ScopeLine(Guid.Parse("00000000-0000-7000-8000-000000000012"), "A-VIS exit", LineRole.Exit, Zone),
            new ScopeLine(Guid.Parse("00000000-0000-7000-8000-000000000013"), "A-VIS count", LineRole.Count, Zone),
            new ScopeLine(Guid.Parse("00000000-0000-7000-8000-000000000014"), "A-OV entry", LineRole.OverflowEntry, Zone),
            new ScopeLine(Guid.Parse("00000000-0000-7000-8000-000000000021"), "A-CIT entry", LineRole.Entry, "A-CIT")
        };
        var observers = new[] { Guid.Parse("00000000-0000-7000-8000-000000000901"), Guid.Parse("00000000-0000-7000-8000-000000000902") };
        var bins = Enumerable.Range(0, 4).Select(i => start.AddMinutes(15 * i)).ToArray();
        int Version() => random.Next(10) == 0 ? 6 : 7;

        var counts = new List<ManualCountRow>();
        var lineBins = new List<LineBinCount>();
        foreach (var line in lines)
        {
            foreach (var bin in bins)
            {
                foreach (var observer in observers.Where(_ => random.Next(3) > 0))
                {
                    for (var revision = 1; revision <= random.Next(1, 3); revision++)
                        counts.Add(new ManualCountRow(line.LineId, bin, observer, revision, random.Next(0, 150), random.Next(0, 150)));
                }

                if (random.Next(4) > 0)
                    lineBins.Add(new LineBinCount(line.QueueZone, line.Name, bin, Version(), random.Next(0, 160), random.Next(0, 160)));
                if (random.Next(8) == 0)
                    lineBins.Add(new LineBinCount(line.QueueZone, line.Name, bin, 7, random.Next(0, 160), 0, LineCountSource.Vendor));
            }
        }

        var queueBins = new List<QueueBinRow>();
        var health = new List<ZoneHealthBin>();
        var minutes = new List<QueueMinuteRow>();
        foreach (var zone in zones)
        {
            foreach (var bin in Enumerable.Range(0, 8).Select(i => start.AddMinutes(15 * i)))
            {
                for (var revision = 1; revision <= random.Next(0, 3); revision++)
                {
                    var entries = random.Next(0, 200);
                    queueBins.Add(new QueueBinRow(zone.Name, bin, TimeSpan.FromMinutes(15), revision, random.Next(5) == 0 ? BinStatus.Provisional : BinStatus.Final,
                        (BinQuality)random.Next(0, 3), Version(), entries, random.Next(0, entries + 1)));
                }

                if (random.Next(4) > 0)
                {
                    var entered = random.Next(0, 40);
                    health.Add(new ZoneHealthBin(zone.Name, bin, TimeSpan.FromMinutes(15), 1, random.Next(5) == 0 ? BinStatus.Provisional : BinStatus.Final, Version(),
                        0, 0, null, null, null, entered, random.Next(0, entered + 1), 0, 0, 0, 0, 0, null, 0, 0, 0));
                }
            }

            foreach (var minute in Enumerable.Range(0, 120).Select(i => start.AddMinutes(i)).Where(_ => random.Next(5) > 0))
            {
                var waits = random.Next(0, 4) == 0 ? 0 : random.Next(1, 30);
                // ARV-104f: most minutes carry a live part, a nowcast or a reason, sometimes flagged.
                var (nowcast, reason, flag) = Live(random);
                minutes.Add(new QueueMinuteRow(zone.Name, minute, Version(), random.Next(6) == 0 ? BinStatus.Provisional : BinStatus.Final, waits,
                    waits == 0 ? null : Math.Round(random.NextDouble() * 40, 3), nowcast, reason, flag));
            }
        }

        // ARV-104f: shadow nowcasts (some with a sensor cycle time, a few unusable), two desks observed by two observers with
        // corrections, and their stored minutes (whole or split, some flagged or partly unknown, a few conflicting or refused).
        var shadows = new List<ShadowMinuteRow>();
        foreach (var zone in zones)
        {
            foreach (var minute in Enumerable.Range(0, 70).Select(i => start.AddMinutes(i)).Where(_ => random.Next(3) > 0))
            {
                var (nowcast, reason, flag) = Live(random);
                shadows.Add(new ShadowMinuteRow(zone.Name, minute, nowcast, reason, flag ?? false,
                    nowcast is not null && random.Next(2) == 0 ? Math.Round(0.5 + (random.NextDouble() * 3), 3) : null));
                if (random.Next(25) == 0)
                    shadows.Add(new ShadowMinuteRow(zone.Name, minute, 1, null, false, 0.01));
            }
        }

        var desks = new[] { new ScopeDesk(Guid.Parse("00000000-0000-7000-8000-000000000031"), "IMM", "D01"), new ScopeDesk(Guid.Parse("00000000-0000-7000-8000-000000000032"), "IMM", "D02") };
        var observations = new List<DeskObservationRow>();
        var deskMinutes = new List<DeskMinuteRow>();
        foreach (var desk in desks)
        {
            foreach (var minute in Enumerable.Range(0, 60).Select(i => start.AddMinutes(i)))
            {
                foreach (var observer in observers.Where(_ => random.Next(3) == 0))
                {
                    for (var revision = 1; revision <= random.Next(1, 3); revision++)
                        observations.Add(new DeskObservationRow(desk.DeskId, minute, observer, revision, random.Next(30) == 0 ? (ObservedDeskState)9 : (ObservedDeskState)random.Next(4)));
                }

                if (random.Next(6) == 0)
                    continue;
                var main = random.Next(0, 61);
                double[] seconds = [0, 0, 0, 0, 0];
                seconds[random.Next(5)] += main;
                seconds[random.Next(5)] += random.Next(0, 61 - main);
                deskMinutes.Add(new DeskMinuteRow(desk.CheckpointCode, desk.DeskCode, minute, seconds[0], seconds[1], seconds[2], seconds[3], seconds[4], random.Next(5) == 0));
                if (random.Next(20) == 0)
                    deskMinutes.Add(new DeskMinuteRow(desk.CheckpointCode, desk.DeskCode, minute, 60, 0, 0, 0, random.Next(2), false));
            }
        }

        var offsets = Enumerable.Range(0, 4).Select(_ => random.Next(-8_000, 8_000)).ToArray();
        var runs = new List<TracerRunRow>();
        for (var n = 1; n <= random.Next(0, 14); n++)
        {
            var batch = random.Next(0, 4);
            var joined = start.AddSeconds(random.Next(0, 3_000));
            var exited = joined.AddSeconds(random.Next(60, 2_400));
            runs.Add(new TracerRunRow(Guid.Parse($"00000000-0000-7000-8000-{1000 + n:D12}"), Guid.Parse($"00000000-0000-7000-8000-{2000 + batch:D12}"),
                observers[batch % 2], zones[random.Next(2)].ZoneId, $"T-{n:D2}", joined.AddMilliseconds(offsets[batch]), exited.AddMilliseconds(offsets[batch]),
                offsets[batch], joined, exited, random.Next(7) == 0));
        }

        var intervals = Enumerable.Range(0, random.Next(0, 3)).Select(_ =>
        {
            var from = start.AddMinutes(random.Next(0, 110));
            return new QualityInterval(zones[random.Next(2)].Name, from, from.AddMinutes(random.Next(1, 20)), random.Next(2) == 0 ? BinQuality.Degraded : BinQuality.Unknown);
        }).ToList();

        // A few rows the engine must leave out, whatever their place in the list.
        if (random.Next(3) == 0)
            counts.Add(new ManualCountRow(lines[0].LineId, start.AddMinutes(7), observers[0], 1, 1, 1));
        if (random.Next(3) == 0)
            minutes.Add(new QueueMinuteRow(Zone, start.AddMinutes(200), 7, BinStatus.Final, 3, double.NaN));

        return new ComparisonInput
        {
            Scope = new ComparisonScope(7, zones, lines, [new UtcWindow(start, start.AddHours(1))]) { Desks = desks },
            ManualCounts = counts,
            TracerRuns = runs,
            LineBins = lineBins,
            QueueMinutes = minutes,
            QueueBins = queueBins,
            HealthBins = health,
            QualityIntervals = intervals,
            DeskObservations = observations,
            DeskMinutes = deskMinutes,
            ShadowMinutes = shadows
        };
    }

    private static readonly string[] Reasons = Enum.GetNames<NoServiceReason>();

    /// <summary>
    /// A live part: none (a quarter of the time), else a nowcast or a reason, sometimes flagged; now and then a nowcast near the
    /// largest double (finite and stored as it is: its error is capped, and the result must still be written as JSON).
    /// </summary>
    private static (double? Nowcast, string Reason, bool? Flag) Live(Draws random)
    {
        if (random.Next(4) == 0)
            return (null, null, null);
        var flag = random.Next(3) == 0;
        if (random.Next(40) == 0)
            return (random.Next(2) == 0 ? double.MaxValue : 1e308, null, flag);
        return random.Next(5) == 0 ? (null, Reasons[random.Next(Reasons.Length)], flag) : (Math.Round(random.NextDouble() * 40, 3), null, flag);
    }

    #endregion
}
