using System.Text.Json;
using Ariva.Core;
using Ariva.Core.Alerting;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using Ariva.Infra.Services.Seed;
using Ariva.Simulation.Api.Scenarios.Engine;
using Ariva.UnitTests.Replay;
using FluentAssertions;

namespace Ariva.UnitTests.Alerting;

/// <summary>
/// ARV-038: the rule evaluator. Sustain windows, skipped minutes, one open alert per target (dedupe), auto-resolve by
/// the clear threshold or the condition ending, the minimum-queue gate, repeated minutes; the live evaluation (one
/// minute at a time, its state kept as JSON between minutes) equals the backtest (one fold) on random series and on the
/// reference evening, where R-001 raises at 18:05 on the Visitors queue as in the prototype; the predicted nowcast warns
/// its lead time ahead of the wave.
/// </summary>
public sealed class AlertEvaluatorTests
{
    private static readonly DateTime T0 = new(2026, 9, 28, 17, 0, 0, DateTimeKind.Utc);

    private static AlertRuleValues Above(int sustain = 1, int clear = 1, double? clearBelow = null, int? minQueue = null) =>
        new("Above 15", ["A-VIS"], AlertMetric.Nowcast, AlertComparator.GreaterThan, 15, minQueue, clearBelow, sustain, clear, AlertSeverity.Warning,
            null, null, null, null, false, true);

    private static IEnumerable<AlertMinute> Series(params double?[] values) =>
        values.Select((v, i) => new AlertMinute(T0.AddMinutes(i), v, 50));

    private static IReadOnlyList<string> Clock(IEnumerable<AlertTransition> transitions) =>
        [.. transitions.Select(t => $"{t.Kind}@{(int)(t.MinuteUtc - T0).TotalMinutes}")];

    [Fact]
    public void Sustain_Should_RaiseOnTheNthMinuteInARow_When_TheConditionHolds()
    {
        var (_, transitions) = AlertEvaluator.Run(Above(sustain: 3), AlertTargetState.Fresh, Series(16, 16, 10, 16, 16, 16, 16));

        Clock(transitions).Should().Equal(["Raised@5"], "a break at minute 2 restarts the count; minutes 3, 4 and 5 make three");
    }

    [Fact]
    public void Minutes_Should_BeSkipped_When_TheyHaveNoValue()
    {
        var (_, transitions) = AlertEvaluator.Run(Above(sustain: 3), AlertTargetState.Fresh, Series(16, null, 16, double.NaN, 16));

        Clock(transitions).Should().Equal(["Raised@4"], "a degraded or missing minute neither counts nor breaks the run");
    }

    [Fact]
    public void Target_Should_HoldOneOpenAlertAndClearByItself_When_TheConditionPersistsThenEnds()
    {
        var (state, transitions) = AlertEvaluator.Run(Above(clear: 2, clearBelow: 12), AlertTargetState.Fresh, Series(16, 20, 30, 14, 11, 13, 11, 11, 16));

        Clock(transitions).Should().Equal(["Raised@0", "Cleared@7", "Raised@8"],
            "one alert while it holds (dedupe); 14 is not below 12; 11 then 13 restarts the clear count; 11 and 11 clear; then it is armed again");
        state.Armed.Should().BeFalse();
    }

    [Fact]
    public void Clear_Should_FollowTheCondition_When_ThereIsNoClearThreshold()
    {
        var (_, transitions) = AlertEvaluator.Run(Above(clear: 1), AlertTargetState.Fresh, Series(16, 15, 16));

        Clock(transitions).Should().Equal(["Raised@0", "Cleared@1", "Raised@2"]);
    }

    [Fact]
    public void Gate_Should_KeepTheRuleQuiet_When_FewerThanTheMinimumAreQueuing()
    {
        var rule = Above(minQueue: 10);
        var minutes = new[] { new AlertMinute(T0, 20, 9), new AlertMinute(T0.AddMinutes(1), 20, null), new AlertMinute(T0.AddMinutes(2), 20, 10) };

        Clock(AlertEvaluator.Run(rule, AlertTargetState.Fresh, minutes).Transitions).Should().Equal(["Raised@2"]);
        Clock(AlertEvaluator.Run(rule with { MinQueueLength = 0 }, AlertTargetState.Fresh, minutes).Transitions).Should().Equal(["Raised@0"], "a gate of 0 gates nothing");
    }

    [Fact]
    public void Below_Should_ClearAboveTheClearThreshold_When_TheRuleFiresBelow()
    {
        var rule = new AlertRuleValues("Throughput dip", ["A-VIS"], AlertMetric.QueueLength, AlertComparator.LessThan, 5, null, 8, 1, 2, AlertSeverity.Info,
            null, null, null, null, false, true);

        Clock(AlertEvaluator.Run(rule, AlertTargetState.Fresh, Series(4, 7, 9, 9, 3)).Transitions).Should().Equal(["Raised@0", "Cleared@3", "Raised@4"]);
    }

    [Fact]
    public void Conditions_Should_RaiseOnTrue_When_ComparedWithIsTrue()
    {
        var rule = new AlertRuleValues("Sensor offline", ["A-VIS"], AlertMetric.SensorOffline, AlertComparator.IsTrue, null, null, null, 1, 1, AlertSeverity.Warning,
            null, 15, null, "Systems", false, true);

        Clock(AlertEvaluator.Run(rule, AlertTargetState.Fresh, Series(0, 1, 1, 0, 0)).Transitions).Should().Equal(["Raised@1", "Cleared@3"]);
    }

    [Fact]
    public void Step_Should_IgnoreAMinute_When_ItWasAlreadyTaken()
    {
        var (state, raised) = AlertEvaluator.Step(Above(), AlertTargetState.Fresh, new AlertMinute(T0, 20, 50));
        var (again, none) = AlertEvaluator.Step(Above(), state, new AlertMinute(T0, 3, 50));
        var (_, earlier) = AlertEvaluator.Step(Above(), state, new AlertMinute(T0.AddMinutes(-5), 3, 50));

        raised.Should().NotBeNull();
        none.Should().BeNull();
        earlier.Should().BeNull();
        again.Should().Be(state, "a repeated or older minute changes nothing (a replayed tick is harmless)");
    }

    [Theory]
    [InlineData(1, 1, null)]
    [InlineData(3, 2, 12.0)]
    [InlineData(5, 4, 10.0)]
    public void Live_Should_EqualTheBacktest_When_MinutesArriveOneByOneWithTheStateStoredBetween(int sustain, int clear, double? clearBelow)
    {
        // A fixed pseudo-random series (a linear congruential sequence, not System.Random: CA5394), with gaps.
        var seed = (uint)(9303 + sustain);
        uint Next() => seed = unchecked(seed * 1664525u + 1013904223u);
        var values = Enumerable.Range(0, 600).Select(_ => Next() % 10 == 0 ? (double?)null : Next() % 31).ToArray();
        var minutes = Series(values).ToList();
        var rule = Above(sustain, clear, clearBelow);

        var backtest = AlertEvaluator.Run(rule, AlertTargetState.Fresh, minutes).Transitions;
        var live = new List<AlertTransition>();
        var stored = JsonSerializer.Serialize(AlertTargetState.Fresh);
        foreach (var minute in minutes)
        {
            var (state, transition) = AlertEvaluator.Step(rule, JsonSerializer.Deserialize<AlertTargetState>(stored), minute);
            if (transition is not null)
                live.Add(transition);
            stored = JsonSerializer.Serialize(state);
        }

        backtest.Should().NotBeEmpty();
        live.Should().Equal(backtest);
    }

    private static readonly Lazy<IReadOnlyList<QueueLiveMinute>> Visitors = new(() =>
        ReferenceReplay.Run(null).Outputs["DMO/A-VIS"].SelectMany(o => o.Live).OrderBy(l => l.MinuteUtc).ToList());

    /// <summary>The stored minutes as <c>AlertInputs</c> reads them for a nowcast rule.</summary>
    private static IEnumerable<AlertMinute> NowcastMinutes(IEnumerable<QueueLiveMinute> live) =>
        live.Select(l => new AlertMinute(l.MinuteUtc, l.LengthDegraded ? null : l.NowcastMinutes, l.QueueLength));

    [Fact]
    public void R001_Should_RaiseAt1805OnTheVisitorsQueue_When_TheReferenceEveningIsJudgedLiveOrBacktested()
    {
        var r001 = DemoAlertRules.Values[0];
        var minutes = NowcastMinutes(Visitors.Value).ToList();

        var backtest = AlertEvaluator.Run(r001, AlertTargetState.Fresh, minutes).Transitions;
        var state = AlertTargetState.Fresh;
        var live = new List<AlertTransition>();
        foreach (var minute in minutes)
        {
            (state, var t) = AlertEvaluator.Step(r001, JsonSerializer.Deserialize<AlertTargetState>(JsonSerializer.Serialize(state)), minute);
            if (t is not null)
                live.Add(t);
        }

        backtest.Should().NotBeEmpty();
        backtest[0].Kind.Should().Be(AlertTransitionKind.Raised);
        backtest[0].MinuteUtc.Should().Be(ReferenceReplay.WallOf(18 * 60 + 5), "R-001: the Visitors nowcast passes 15 minutes at 18:05 with more than 10 queuing");
        live.Should().Equal(backtest, "the live evaluation is the same fold, minute by minute");

        var prototype = ScenarioDay.Run(ScenarioConfig.Reference()).Alerts.First(a => a.Id.StartsWith("R-001:A-VIS:", StringComparison.Ordinal));
        ReferenceReplay.WallOf(prototype.RaisedAt).Should().Be(backtest[0].MinuteUtc, "the prototype raises R-001 on the Visitors queue at the same minute");
    }

    [Fact]
    public void PredictedWait_Should_FollowF8_When_TheWaveIsAddedAndTheDesksClear()
    {
        // 10 arrive and 5 are served each minute: the queue grows by 5 a minute, its peak at the horizon's end.
        PredictedWait.Peak(20, 5, Enumerable.Repeat(10.0, 15).ToList(), 15).Should().Be(new PredictedPeak((20 + 15 * 5 + 1) / 5.0, 15));
        PredictedWait.Nowcast(20, 5, Enumerable.Repeat(0.0, 15).ToList(), 15).Should().BeApproximately(3.2, 1e-9, "no one arriving: the peak is the first minute, (15 + 1) / 5");
        PredictedWait.Nowcast(0, 5, Enumerable.Repeat(0.0, 15).ToList(), 15).Should().Be(0.2, "never below an empty queue: (0 + 1) / 5");
        // A wave of 100 in minute 3 that the desks have cleared by minute 15 is still seen at its peak.
        PredictedWait.Peak(0, 10, [0, 0, 100, .. Enumerable.Repeat(0.0, 12)], 15).Should().Be(new PredictedPeak((90 + 1) / 10.0, 3));
        PredictedWait.Nowcast(20, 0, Enumerable.Repeat(1.0, 15).ToList(), 15).Should().BeNull("no throughput, no nowcast");
        PredictedWait.Nowcast(20, 5, Enumerable.Repeat(1.0, 14).ToList(), 15).Should().BeNull("the projection must cover the lead time");
        PredictedWait.Nowcast(20, 5, [.. Enumerable.Repeat(1.0, 14), double.NaN], 15).Should().BeNull();
        PredictedWait.Nowcast(20, 5, null, 15).Should().BeNull("no arrival wave before ARV-047");
    }

    [Theory]
    [InlineData(15)]
    [InlineData(60)]
    public void Prediction_Should_WarnItsLeadTimeAhead_When_AWaveIsComing(int lead)
    {
        var rule = new AlertRuleValues("Wave ahead", ["A-VIS"], AlertMetric.PredictedNowcast, AlertComparator.GreaterThan, 15, null, null, 1, 1, AlertSeverity.Warning,
            null, null, null, null, false, true, lead);
        rule.Problems().Should().BeEmpty();
        // 30 queuing, 6 an hour... 6 a minute served; 300 passengers arrive between 18:20 and 18:44 (a 25-minute wave).
        double Arrivals(DateTime m) => m >= T0.AddMinutes(80) && m < T0.AddMinutes(105) ? 12 : 0;
        var minutes = Enumerable.Range(0, 120).Select(i =>
        {
            var at = T0.AddMinutes(i);
            var ahead = Enumerable.Range(1, lead).Select(k => Arrivals(at.AddMinutes(k))).ToList();
            var peak = PredictedWait.Peak(30, 6, ahead, lead);
            return new AlertMinute(at, peak?.Minutes, 30, null, at.AddMinutes(peak!.AheadMinutes));
        }).ToList();

        var first = AlertEvaluator.Run(rule, AlertTargetState.Fresh, minutes).Transitions.First();

        first.Kind.Should().Be(AlertTransitionKind.Raised);
        var warning = (T0.AddMinutes(80) - first.MinuteUtc).TotalMinutes;
        warning.Should().BeGreaterThan(0, "the warning comes before the wave reaches the hall").And.BeLessThanOrEqualTo(lead);
        first.PredictedForUtc.Should().BeAfter(T0.AddMinutes(80), "the breach it warns of is inside the wave").And.BeOnOrBefore(first.MinuteUtc.AddMinutes(lead));
        ((30 + 1) / 6.0).Should().BeLessThan(15, "the queue now is short: only the projection sees the breach");
    }

    [Fact]
    public void Backtest_Should_BeLimitedAndForCreatorsWhoSeeLiveQueues_When_TheEndpointIsInspected()
    {
        var action = typeof(Ariva.Api.Main.Controllers.AdminArea.Alerting.AlertRulesController).GetMethod("Backtest")!;
        var limit = action.GetCustomAttributes(typeof(Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute), false)
            .Cast<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>().Single();

        limit.PolicyName.Should().Be(Ariva.Api.Common.Extensions.RateLimitingExtensions.BacktestPolicy, "two at a time per host (CWE-400)");
        // The service also requires the live queue: every default role that may create rules sees live queues, so the check refuses no creator.
        Ariva.Core.Security.RolePermissions.ByRole.Values.Where(p => p.Contains(Ariva.Core.Global.Defaults.Permissions.CreateAlertRule))
            .Should().NotBeEmpty().And.OnlyContain(p => p.Contains(Ariva.Core.Global.Defaults.Permissions.ViewLiveQueue));
    }

    [Theory]
    [InlineData(AlertMetric.PredictedNowcast, null)]
    [InlineData(AlertMetric.PredictedNowcast, 14)]
    [InlineData(AlertMetric.PredictedNowcast, 61)]
    [InlineData(AlertMetric.Nowcast, 15)]
    public void Lead_Should_BeRefused_When_ItIsMissingOutOfRangeOrOnAnotherMetric(AlertMetric metric, int? lead)
    {
        var rule = new AlertRuleValues("Lead", ["A-VIS"], metric, AlertComparator.GreaterThan, 15, null, null, 1, 1, AlertSeverity.Warning,
            null, null, null, null, false, true, lead);

        rule.Problems().Should().Contain(p => p.Contains("lead", StringComparison.OrdinalIgnoreCase));
    }
}
