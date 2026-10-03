using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Alerting;

/// <summary>
/// One minute of one rule target (a queue zone, or a device for <see cref="AlertMetric.SensorOffline"/>): the metric's
/// value at the end of the minute, null when there is nothing to judge (no row, a degraded nowcast, no projection), the
/// queue length for a minimum-queue gate, and what the value refers to (the bin of a P90, the minute a prediction is for).
/// </summary>
public sealed record AlertMinute(DateTime MinuteUtc, double? Value, int? QueueLength = null, DateTime? BinStartUtc = null, DateTime? PredictedForUtc = null);

/// <summary>
/// Where one rule target stands between minutes: armed (no open alert) with the minutes the condition has held, or
/// disarmed (an alert is open) with the minutes the clear condition has held, and the last minute taken. Plain data,
/// so the live evaluation keeps it between ticks and a backtest starts from <see cref="Fresh"/>.
/// </summary>
public sealed record AlertTargetState(bool Armed, int Sustained, int Clearing, DateTime? LastMinuteUtc)
{
    public static AlertTargetState Fresh { get; } = new(true, 0, 0, null);
}

public enum AlertTransitionKind
{
    Raised,
    Cleared
}

/// <summary>An alert raised or cleared at the end of <see cref="MinuteUtc"/>, with the value that decided it.</summary>
public sealed record AlertTransition(AlertTransitionKind Kind, DateTime MinuteUtc, double Value, DateTime? BinStartUtc, DateTime? PredictedForUtc);

/// <summary>
/// The evaluation of one alert rule on one target, minute by minute (ARV-038), the prototype's rule engine
/// (<c>ScenarioDay.EvaluateRule</c>) on Ariva's stored minutes:
/// <list type="bullet">
/// <item>a minute without a value is skipped: it neither counts towards nor breaks a sustain or clear run;</item>
/// <item>armed, the condition (with the nowcast's minimum queue length) must hold for the sustain minutes in a row to
/// raise; one alert per rule and target, so a raised target is disarmed (dedupe);</item>
/// <item>disarmed, the clear condition (beyond the clear threshold on the clearing side, or the condition no longer
/// holding when there is none) must hold for the clear minutes in a row to clear (auto-resolve) and re-arm.</item>
/// </list>
/// Live evaluation and backtest are the same fold over the same minutes, so a backtest from a fresh state over a range
/// gives exactly the alerts the live evaluation raised over it from the same start. Pure: no I/O, no clock.
/// </summary>
public static class AlertEvaluator
{
    /// <summary>Takes one minute; minutes at or before the last one taken are ignored, so a repeated minute changes nothing.</summary>
    public static (AlertTargetState State, AlertTransition Transition) Step(AlertRuleValues rule, AlertTargetState state, AlertMinute minute)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(minute);
        if (state.LastMinuteUtc is { } last && minute.MinuteUtc <= last)
            return (state, null);
        var next = state with { LastMinuteUtc = minute.MinuteUtc };
        if (minute.Value is not { } value || !double.IsFinite(value))
            return (next, null);

        var holds = Holds(rule, value, minute.QueueLength);
        if (next.Armed)
        {
            if (!holds)
                return (next with { Sustained = 0 }, null);
            var sustained = next.Sustained + 1;
            return sustained >= Math.Max(1, rule.SustainMinutes)
                ? (next with { Armed = false, Sustained = 0, Clearing = 0 }, new AlertTransition(AlertTransitionKind.Raised, minute.MinuteUtc, value, minute.BinStartUtc, minute.PredictedForUtc))
                : (next with { Sustained = sustained }, null);
        }

        if (!Clears(rule, value, holds))
            return (next with { Clearing = 0 }, null);
        var clearing = next.Clearing + 1;
        return clearing >= Math.Max(1, rule.ClearAfterMinutes)
            ? (next with { Armed = true, Sustained = 0, Clearing = 0 }, new AlertTransition(AlertTransitionKind.Cleared, minute.MinuteUtc, value, minute.BinStartUtc, minute.PredictedForUtc))
            : (next with { Clearing = clearing }, null);
    }

    /// <summary>Folds minutes (in time order) from <paramref name="state"/>; the transitions in order and the state after the last.</summary>
    public static (AlertTargetState State, IReadOnlyList<AlertTransition> Transitions) Run(AlertRuleValues rule, AlertTargetState state, IEnumerable<AlertMinute> minutes)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        var transitions = new List<AlertTransition>();
        foreach (var minute in minutes.Where(m => m is not null).OrderBy(m => m.MinuteUtc))
        {
            (state, var transition) = Step(rule, state, minute);
            if (transition is not null)
                transitions.Add(transition);
        }

        return (state, transitions);
    }

    /// <summary>The rule's condition on a value: the comparison with the threshold, and for the nowcast the minimum queue.</summary>
    public static bool Holds(AlertRuleValues rule, double value, int? queueLength)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var threshold = rule.Threshold ?? 0;
        var compared = rule.Comparator switch
        {
            AlertComparator.GreaterThan => value > threshold,
            AlertComparator.GreaterOrEqual => value >= threshold,
            AlertComparator.LessThan => value < threshold,
            AlertComparator.LessOrEqual => value <= threshold,
            _ => value > 0
        };
        // A gate of 0 people gates nothing (as in the reference); a gate without a known queue length does not hold.
        return compared && (rule.MinQueueLength is not { } gate || gate == 0 || (queueLength is { } length && length >= gate));
    }

    /// <summary>The clear condition: beyond the clear threshold on the clearing side, or the condition no longer holding.</summary>
    public static bool Clears(AlertRuleValues rule, double value, bool holds)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.ClearThreshold is not { } clear)
            return !holds;
        return rule.Comparator is AlertComparator.LessThan or AlertComparator.LessOrEqual ? value > clear : value < clear;
    }
}

/// <summary>The highest nowcast projected within the lead time, and how many minutes ahead it is.</summary>
public sealed record PredictedPeak(double Minutes, int AheadMinutes);

/// <summary>
/// The nowcast a queue zone is projected to reach within <c>lead</c> minutes (ARV-038, predicted breach): the queue is
/// stepped a minute at a time, adding that minute's projected arrivals and taking away what the desks clear at the
/// current throughput (never below an empty queue), and each minute's F8 nowcast ((Q + 1) / throughput) is taken; the
/// highest one is the prediction, the first minute it is reached the one it is for. Looking at the whole horizon, not
/// only its last minute, a wave that the desks would clear before the horizon's end is still seen at its peak. Null
/// without a throughput or a full projection.
/// </summary>
public static class PredictedWait
{
    public static PredictedPeak Peak(int queueLength, double? throughputPerMinute, IReadOnlyList<double> arrivals, int lead)
    {
        if (throughputPerMinute is not { } mu || !double.IsFinite(mu) || mu <= 0 || arrivals is null || lead < 1 || arrivals.Count < lead || queueLength < 0)
            return null;
        double queue = queueLength;
        PredictedPeak peak = null;
        for (var k = 1; k <= lead; k++)
        {
            var a = arrivals[k - 1];
            if (!double.IsFinite(a) || a < 0)
                return null;
            queue = Math.Max(0, queue + a - mu);
            var nowcast = (queue + 1) / mu;
            if (peak is null || nowcast > peak.Minutes)
                peak = new PredictedPeak(nowcast, k);
        }

        return peak;
    }

    /// <summary>The peak's nowcast alone.</summary>
    public static double? Nowcast(int queueLength, double? throughputPerMinute, IReadOnlyList<double> arrivals, int lead) =>
        Peak(queueLength, throughputPerMinute, arrivals, lead)?.Minutes;
}

/// <summary>
/// The arrival-wave projection a predicted-breach rule reads (F14): projected arrivals per minute at a queue zone. The
/// hosts read the AODB and AMAN projection (ARV-047, <c>Ariva.Infra.Flights.ProjectedArrivalWave</c>); where there is
/// none (<see cref="NoArrivalWave"/>, or a zone serving no lane) predicted rules have nothing to judge.
/// </summary>
public interface IArrivalWaveSource
{
    /// <summary>Projected arrivals per minute for the minutes after <paramref name="fromUtc"/> up to <paramref name="toUtc"/>; null when there is no projection.</summary>
    Task<IReadOnlyDictionary<DateTime, double>> ArrivalsAsync(string siteCode, string zoneName, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
}

/// <summary>No projection (tests and evaluations without flights).</summary>
public sealed class NoArrivalWave : IArrivalWaveSource
{
    public Task<IReadOnlyDictionary<DateTime, double>> ArrivalsAsync(string siteCode, string zoneName, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<DateTime, double>>(null);
}
