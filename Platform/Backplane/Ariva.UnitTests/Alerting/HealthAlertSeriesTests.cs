using System.Text.Json;
using Ariva.Core.Alerting;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using FluentAssertions;

namespace Ariva.UnitTests.Alerting;

/// <summary>
/// ARV-114b: the health-check alert metrics (ConservationResidual, TrackCompletion, OccupancyOutOfRange) on the stored
/// continuous checks of F18. Each metric's value and unit, when a bin's value is settled, which bin a minute is judged on,
/// how far the live evaluation may go, and the constructed case: bins whose rows are written provisional, then with their
/// residual, then final, at different times after their end, judged live a tick a minute with the state stored as JSON
/// between ticks, raise and clear exactly what the backtest over the final rows gives; a live evaluation that did not wait
/// for the bins would not.
/// </summary>
public sealed class HealthAlertSeriesTests
{
    #region Helpers

    private static readonly DateTime T0 = new(2026, 9, 28, 17, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int minutes) => T0.AddMinutes(minutes);

    private static HealthBinReading Row(int start, BinStatus status = BinStatus.Final, long? residual = 0, int? occupancyEnd = 10, long entered = 20, long exited = 20,
        int capacityMinutes = 15, int outside = 0, int length = 15) =>
        new(At(start), length, status, residual, occupancyEnd, entered, exited, capacityMinutes, outside);

    private static AlertRuleValues Rule(AlertMetric metric, AlertComparator comparator, double threshold, int sustain = 1, int clear = 1, double? clearAt = null) =>
        new("Health", ["A-VIS"], metric, comparator, threshold, null, clearAt, sustain, clear, AlertSeverity.Warning, null, null, null, null, false, true);

    private static IEnumerable<(DateTime, int?)> QueueMinutes(int from, int to) => Enumerable.Range(from, to - from + 1).Select(m => (At(m), (int?)5));

    #endregion

    #region Values and units

    [Theory]
    [InlineData(0L, 0.0)]
    [InlineData(3L, 3.0)]
    [InlineData(-3L, 3.0)]
    [InlineData(long.MinValue, 9.223372036854775808e18)]
    public void ValueOf_Should_BeTheResidualsSizeInPeople_When_TheResidualIsKnown(long residual, double size)
    {
        HealthAlertSeries.ValueOf(AlertMetric.ConservationResidual, Row(0, BinStatus.Provisional, residual: residual)).Should().Be(size);
    }

    [Theory]
    [InlineData(20L, 18L, 90.0)]
    [InlineData(20L, 17L, 85.0)]
    [InlineData(100L, 29L, 29.0)]
    [InlineData(3L, 1L, 100.0 / 3)]
    [InlineData(10L, 12L, 100.0)]
    public void ValueOf_Should_BeTheCompletionInPercent_When_TheBinIsFinal(long entered, long exited, double percent)
    {
        // 100 x exited / entered in one division: 18 of 20 is exactly 90 and 29 of 100 exactly 29 (0.29 x 100 is not).
        HealthAlertSeries.ValueOf(AlertMetric.TrackCompletion, Row(0, entered: entered, exited: exited)).Should().Be(percent);
    }

    [Fact]
    public void ValueOf_Should_GiveNothing_When_ThereIsNothingToJudge()
    {
        HealthAlertSeries.ValueOf(AlertMetric.TrackCompletion, Row(0, entered: 0, exited: 0)).Should().BeNull("no track entered: no rate, never 0 or 100");
        HealthAlertSeries.ValueOf(AlertMetric.ConservationResidual, Row(0, residual: null)).Should().BeNull("a boundary without a sensor occupancy");
        HealthAlertSeries.ValueOf(AlertMetric.OccupancyOutOfRange, Row(0, capacityMinutes: 0)).Should().BeNull("no minute was checked against a capacity");
        HealthAlertSeries.ValueOf(AlertMetric.OccupancyOutOfRange, Row(0, capacityMinutes: 0, outside: 1)).Should().Be(1, "a reading below 0 is outside without a capacity");
        HealthAlertSeries.ValueOf(AlertMetric.OccupancyOutOfRange, Row(0, capacityMinutes: 15, outside: 0)).Should().Be(0);
        HealthAlertSeries.ValueOf(AlertMetric.OccupancyOutOfRange, Row(0, capacityMinutes: 15, outside: 4)).Should().Be(4);
        HealthAlertSeries.ValueOf(AlertMetric.Nowcast, Row(0)).Should().BeNull("not a health check");
    }

    #endregion

    #region Settled

    [Fact]
    public void Settled_Should_FollowEachMetricsRule_When_TheRowIsStillProvisional()
    {
        var running = Row(0, BinStatus.Provisional, residual: null, occupancyEnd: null, entered: 20, exited: 5);
        var ended = running with { ConservationResidual = 2, OccupancyEnd = 7 };
        var endedNoStart = running with { OccupancyEnd = 7 };

        foreach (var metric in new[] { AlertMetric.ConservationResidual, AlertMetric.TrackCompletion, AlertMetric.OccupancyOutOfRange })
        {
            HealthAlertSeries.Settled(metric, running).Should().BeFalse($"{metric}: the bin has not ended");
            HealthAlertSeries.Settled(metric, running with { Status = BinStatus.Final }).Should().BeTrue($"{metric}: a final row never changes");
        }

        HealthAlertSeries.Settled(AlertMetric.ConservationResidual, ended).Should().BeTrue("both boundaries are measured");
        HealthAlertSeries.Settled(AlertMetric.ConservationResidual, endedNoStart).Should().BeFalse("the start had no sample: wait for the final row");
        HealthAlertSeries.Settled(AlertMetric.OccupancyOutOfRange, endedNoStart).Should().BeTrue("the end boundary was sampled, so every minute was checked");
        HealthAlertSeries.Settled(AlertMetric.TrackCompletion, ended).Should().BeFalse("15 tracks are still queuing: open tracks are not lost");
        HealthAlertSeries.Settled(AlertMetric.TrackCompletion, ended with { TracksExited = 20 }).Should().BeTrue("every track exited: 100 percent whatever comes");
        HealthAlertSeries.Settled(AlertMetric.TrackCompletion, ended with { TracksEntered = 0, TracksExited = 0 }).Should().BeTrue("no track entered and none can now");
        HealthAlertSeries.ValueOf(AlertMetric.TrackCompletion, ended).Should().BeNull("not settled");
        HealthAlertSeries.ValueOf(AlertMetric.ConservationResidual, ended).Should().Be(2);
    }

    #endregion

    #region Which bin, how far

    [Fact]
    public void Minutes_Should_TakeTheBinThatEndedLastByTheMinutesEnd_When_TheZoneHasBins()
    {
        var rows = new[] { Row(0, residual: 1), Row(15, residual: -6), Row(45, residual: 2) };

        var minutes = HealthAlertSeries.Minutes(AlertMetric.ConservationResidual, QueueMinutes(13, 61), rows);
        double? ValueAt(int minute) => minutes.Single(m => m.MinuteUtc == At(minute)).Value;
        DateTime? BinAt(int minute) => minutes.Single(m => m.MinuteUtc == At(minute)).BinStartUtc;

        ValueAt(13).Should().BeNull("17:13 ends before the 17:00 bin does, and there is no earlier bin");
        ValueAt(14).Should().Be(1, "17:14 ends at 17:15, when the 17:00 bin ends");
        BinAt(14).Should().Be(At(0));
        ValueAt(28).Should().Be(1);
        ValueAt(29).Should().Be(6, "the 17:15 bin, from its last minute");
        ValueAt(43).Should().Be(6);
        ValueAt(44).Should().BeNull("the 17:30 bin has no stored row: no value, never the bin before it");
        ValueAt(58).Should().BeNull();
        ValueAt(59).Should().Be(2);
        minutes.Should().OnlyContain(m => m.QueueLength == 5);
    }

    [Fact]
    public void SettledThrough_Should_StopBeforeTheFirstMinuteOfAnUnsettledBin_When_ItEndedWithinTheLimit()
    {
        var rows = new[]
        {
            Row(0), Row(15, BinStatus.Provisional, residual: 3, exited: 10), Row(30, BinStatus.Provisional, residual: null, occupancyEnd: null), Row(45, BinStatus.Provisional, residual: null, occupancyEnd: null)
        };

        HealthAlertSeries.SettledThrough(AlertMetric.ConservationResidual, At(50), rows).Should().Be(At(43),
            "the 17:30 bin governs from 17:44 and is not settled; the 17:45 bin has not ended");
        HealthAlertSeries.SettledThrough(AlertMetric.ConservationResidual, At(40), rows).Should().Be(At(40), "nothing unsettled has ended by 17:41");
        HealthAlertSeries.SettledThrough(AlertMetric.TrackCompletion, At(50), rows).Should().Be(At(28), "the 17:15 bin is not final: its tracks may still be queuing");
        HealthAlertSeries.SettledThrough(AlertMetric.ConservationResidual, At(43), rows).Should().Be(At(43), "the 17:30 bin's first minute is 17:44");
        HealthAlertSeries.SettledThrough(AlertMetric.ConservationResidual, At(44), rows).Should().Be(At(43));
    }

    [Fact]
    public void SettledThrough_Should_StopWaiting_When_ABinStaysUnsettledBeyondTheLimit()
    {
        var stuck = new[] { Row(0, BinStatus.Provisional, residual: null, occupancyEnd: null) };
        var limit = (int)HealthAlertSeries.SettleLimit.TotalMinutes;

        HealthAlertSeries.SettledThrough(AlertMetric.ConservationResidual, At(15 + limit - 2), stuck).Should().Be(At(13), "within the limit it holds the evaluation back");
        HealthAlertSeries.SettledThrough(AlertMetric.ConservationResidual, At(15 + limit - 1), stuck).Should().Be(At(15 + limit - 1), "beyond it, its minutes are judged without a value");
        HealthAlertSeries.Minutes(AlertMetric.ConservationResidual, QueueMinutes(14, 20), stuck).Should().OnlyContain(m => m.Value == null);
    }

    [Fact]
    public void Ordered_Should_LeaveOutRowsThatCannotBeABin_When_TheyAreRead()
    {
        var rows = new[]
        {
            Row(0, length: 0), Row(0, length: 1441), Row(15), null, Row(0) with { StartUtc = DateTime.MaxValue.AddMinutes(-10) },
            Row(0) with { StartUtc = DateTime.MinValue }
        };

        HealthAlertSeries.Ordered(rows).Should().ContainSingle().Which.StartUtc.Should().Be(At(15));
        HealthAlertSeries.Governing(HealthAlertSeries.Ordered(rows), DateTime.MaxValue).Should().BeNull("a minute at the calendar's end is not placed");
        HealthAlertSeries.SettledThrough(AlertMetric.TrackCompletion, DateTime.MinValue, rows).Should().Be(DateTime.MinValue, "nothing to wait for at the calendar's start");
    }

    #endregion

    [Fact]
    public void Clusters_Should_KeepAZoneFarBehindInItsOwnRead_When_ReadsAreGrouped()
    {
        // ARV-114b review (CWE-120): one read per group of nearby times, never one window over the month between them.
        var latest = new Dictionary<string, DateTime> { ["A-VIS"] = At(230), ["D-VIS"] = At(-30 * 1440), ["A-CIT"] = At(229), ["A-CRW"] = At(50) };

        var clusters = Ariva.Infra.Alerting.AlertInputs.Clusters(latest, l => l.Value, HealthAlertSeries.SettleLimit);

        clusters.Select(c => string.Join(",", c.Select(l => l.Key))).Should().Equal("D-VIS", "A-CRW,A-CIT,A-VIS");
        clusters.Should().OnlyContain(c => c[c.Count - 1].Value - c[0].Value <= HealthAlertSeries.SettleLimit);
        Ariva.Infra.Alerting.AlertInputs.Clusters(new[] { At(0), At(181), At(180) }, t => t, HealthAlertSeries.SettleLimit).Select(c => c.Count).Should().Equal(2, 1);
        Ariva.Infra.Alerting.AlertInputs.Clusters(Array.Empty<DateTime>(), t => t, HealthAlertSeries.SettleLimit).Should().BeEmpty();
    }

    [Fact]
    public void LongestBin_Should_BeTheBinLengthTheStreamRunsOn_When_TheHealthReadsAreBoundedByIt()
    {
        // ARV-114b re-check: the health reads look back two bins of AlertInputs.LongestBin. Bins are never configured per host
        // or site (the stream's zone settings are the defaults with a site's timing), so every bin is 15 minutes; a change
        // that makes them configurable fails here and must widen the bound.
        Ariva.Infra.Alerting.AlertInputs.LongestBin.Should().Be(TimeSpan.FromMinutes(15));
        new ZoneProcessorSettings().Bins.BinLength.Should().Be(Ariva.Infra.Alerting.AlertInputs.LongestBin);
        var stream = new Ariva.Infra.Streaming.StreamSettings();
        foreach (var site in new string[] { null, "DMO", "AUH-TA" })
            stream.ZoneSettingsFor(site, new ZoneProcessorSettings()).Bins.BinLength.Should().Be(Ariva.Infra.Alerting.AlertInputs.LongestBin, site ?? "the defaults");
        typeof(Ariva.Infra.Streaming.StreamSettings).GetProperties().Should().NotContain(p => p.PropertyType == typeof(BinSettings) || p.PropertyType == typeof(ZoneProcessorSettings),
            "the stream's configuration has no bin settings of its own");
    }

    #region Live equals backtest (constructed case)

    /// <summary>
    /// One bin of the constructed case: written provisional when it opens, with its residual and end sample
    /// <paramref name="endedAfter"/> minutes after its end, and final <paramref name="finalAfter"/> minutes after its end.
    /// </summary>
    private sealed record Plan(int Start, long Residual, long Entered, long Exited, int Outside, int EndedAfter, int FinalAfter);

    private static readonly Plan[] Constructed =
    [
        new(0, 0, 20, 20, 0, 0, 6),
        new(15, 7, 20, 15, 2, 1, 19),
        new(30, -9, 40, 39, 3, 3, 4),
        new(45, 1, 20, 17, 0, 0, 30),
        new(60, 0, 10, 6, 0, 2, 12),
        new(75, 12, 30, 29, 5, 1, 9),
        new(90, 0, 0, 0, 0, 0, 1),
        new(105, -5, 20, 20, 0, 4, 25),
        new(120, 0, 20, 19, 0, 0, 3),
        new(135, 6, 20, 10, 1, 2, 40)
    ];

    /// <summary>What the stream had stored of each bin by the end of minute <paramref name="minute"/>.</summary>
    private static IReadOnlyList<HealthBinReading> StoredBy(int minute) =>
    [
        .. Constructed.Where(p => p.Start <= minute).Select(p =>
        {
            var end = p.Start + 15;
            if (minute >= end + p.FinalAfter)
                return Row(p.Start, BinStatus.Final, p.Residual, 8, p.Entered, p.Exited, 15, p.Outside);
            if (minute >= end + p.EndedAfter)
                return Row(p.Start, BinStatus.Provisional, p.Residual, 8, p.Entered, Math.Min(p.Exited, p.Entered / 2), 15, p.Outside);
            // While the bin runs: no end sample, no residual, its minutes so far.
            return Row(p.Start, BinStatus.Provisional, null, null, p.Entered / 2, 0, Math.Max(0, minute - p.Start), 0);
        })
    ];

    public static TheoryData<AlertMetric, AlertComparator, double, int, int, double?> Rules => new()
    {
        { AlertMetric.ConservationResidual, AlertComparator.GreaterThan, 4, 2, 3, null },
        { AlertMetric.ConservationResidual, AlertComparator.GreaterOrEqual, 6, 1, 1, 2.0 },
        { AlertMetric.TrackCompletion, AlertComparator.LessThan, 90, 1, 1, null },
        { AlertMetric.TrackCompletion, AlertComparator.LessThan, 90, 3, 5, 95.0 },
        { AlertMetric.OccupancyOutOfRange, AlertComparator.GreaterThan, 0, 1, 2, null },
        { AlertMetric.OccupancyOutOfRange, AlertComparator.GreaterOrEqual, 3, 2, 1, null }
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void Live_Should_EqualTheBacktest_When_TheBinsSettleAtDifferentTimesAfterTheirEnd(AlertMetric metric, AlertComparator comparator, double threshold, int sustain,
        int clear, double? clearAt)
    {
        var rule = Rule(metric, comparator, threshold, sustain, clear, clearAt);
        const int Last = 230;

        // The backtest: the final rows, every minute from the start.
        var backtest = AlertEvaluator.Run(rule, AlertTargetState.Fresh, HealthAlertSeries.Minutes(metric, QueueMinutes(0, Last), StoredBy(10_000))).Transitions;

        // Live: a tick after every minute; the stream has stored the queue minutes up to it and the rows as they were then.
        var stored = JsonSerializer.Serialize(AlertTargetState.Fresh with { LastMinuteUtc = At(-1) });
        var live = new List<AlertTransition>();
        for (var tick = 0; tick <= Last + 60; tick++)
        {
            var rows = StoredBy(tick);
            var latest = Math.Min(tick, Last);
            var upTo = HealthAlertSeries.SettledThrough(metric, At(latest), rows);
            var state = JsonSerializer.Deserialize<AlertTargetState>(stored)!;
            var from = state.LastMinuteUtc ?? At(-1);
            foreach (var minute in HealthAlertSeries.Minutes(metric, QueueMinutes(0, latest), rows).Where(m => m.MinuteUtc > from && m.MinuteUtc <= upTo))
            {
                (state, var transition) = AlertEvaluator.Step(rule, state, minute);
                if (transition is not null)
                    live.Add(transition);
            }

            stored = JsonSerializer.Serialize(state);
        }

        backtest.Should().NotBeEmpty("the constructed case raises every rule at least once");
        live.Should().Equal(backtest, $"{metric}: live, waiting for each bin to settle, judges the minutes the backtest judges with the same values");
    }

    [Fact]
    public void Live_Should_DifferFromTheBacktest_When_ItDoesNotWaitForTheBinsToSettle()
    {
        var rule = Rule(AlertMetric.TrackCompletion, AlertComparator.LessThan, 90);
        var backtest = AlertEvaluator.Run(rule, AlertTargetState.Fresh, HealthAlertSeries.Minutes(rule.Metric, QueueMinutes(0, 230), StoredBy(10_000))).Transitions;
        var state = AlertTargetState.Fresh;
        var naive = new List<AlertTransition>();
        for (var tick = 0; tick <= 230; tick++)
        {
            var minute = HealthAlertSeries.Minutes(rule.Metric, QueueMinutes(tick, tick), StoredBy(tick)).Single();
            (state, var transition) = AlertEvaluator.Step(rule, state, minute);
            if (transition is not null)
                naive.Add(transition);
        }

        naive.Should().NotEqual(backtest, "a minute judged before its bin was final has no value then, and is never judged again");
    }

    #endregion
}
