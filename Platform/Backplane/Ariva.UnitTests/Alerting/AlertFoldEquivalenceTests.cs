using Ariva.Core.Alerting;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Infra.Alerting;
using Ariva.UnitTests.Properties;
using CsCheck;
using FluentAssertions;

namespace Ariva.UnitTests.Alerting;

/// <summary>
/// ARV-228b: the alert fold in place, the predicted peak in locals and a sensor rule's minutes computed when taken give exactly
/// what they gave before. Each is held to its previous code, kept here word for word as the reference (<see cref="Before"/>), on
/// random inputs: minutes in any order with repeats, missing and non-finite values and queue lengths, any rule (every comparator,
/// a clear threshold or none, a minimum queue or none) and any starting state, through <see cref="AlertEvaluator.Run"/> (a list in
/// order, a list out of order and a sequence that is no list) and through <see cref="AlertEvaluator.Step"/> one minute at a time.
/// </summary>
public sealed class AlertFoldEquivalenceTests
{
    private static readonly int Iter = 5 * Fuzz.Iterations;

    private static readonly DateTime T0 = new(2026, 9, 28, 17, 0, 0, DateTimeKind.Utc);

    #region The fold

    private static readonly Gen<AlertRuleValues> Rules =
        Gen.Select(Gen.Enum<AlertComparator>(), Gen.Double[0, 30].Nullable(), Gen.Int[0, 30].Nullable(), Gen.Double[0, 30].Nullable(), Gen.Int[0, 4], Gen.Int[0, 4])
            .Select((comparator, threshold, minQueue, clear, sustain, clearAfter) => new AlertRuleValues("rule", ["A-VIS"], AlertMetric.Nowcast, comparator, threshold,
                minQueue, clear, sustain, clearAfter, AlertSeverity.Warning, null, null, null, null, false, true));

    // A value: mostly in the thresholds' range, sometimes missing or not finite (a minute with nothing to judge).
    private static readonly Gen<double?> Values = Gen.Frequency(
        (12, Gen.Double[0, 30].Select(v => (double?)v)),
        (2, Gen.Const((double?)null)),
        (1, Gen.OneOfConst<double?>(double.NaN, double.PositiveInfinity, double.NegativeInfinity)));

    // Minutes within an hour, so times repeat and come out of order.
    private static readonly Gen<AlertMinute> Minutes = Gen.Select(Gen.Int[0, 60], Values, Gen.Int[0, 30].Nullable())
        .Select((at, value, queue) => new AlertMinute(T0.AddMinutes(at), value, queue, T0.AddMinutes(at - 15), T0.AddMinutes(at + 10)));

    private static readonly Gen<AlertTargetState> States = Gen.Select(Gen.Bool, Gen.Int[0, 4], Gen.Int[0, 4], Gen.Int[-1, 60])
        .Select((armed, sustained, clearing, last) => new AlertTargetState(armed, sustained, clearing, last < 0 ? null : T0.AddMinutes(last)));

    [Fact]
    public void Run_Should_GiveThePreviousFoldsTransitionsAndState_When_MinutesComeInAnyOrder()
    {
        Gen.Select(Rules, States, Minutes.Array[0, 80]).Sample((rule, state, minutes) =>
        {
            var expected = Before.Run(rule, state, minutes);
            var sorted = minutes.OrderBy(m => m.MinuteUtc).ToList();

            foreach (var (name, input) in new (string, IEnumerable<AlertMinute>)[]
                     {
                         ("an array as generated", minutes), ("a list in time order", sorted), ("a sequence that is no list", minutes.Select(m => m))
                     })
            {
                var (after, transitions) = AlertEvaluator.Run(rule, state, input);
                after.Should().Be(expected.State, name);
                transitions.Should().Equal(expected.Transitions, name);
            }
        }, iter: Iter);
    }

    [Fact]
    public void Step_Should_GiveThePreviousStepsStateAndTransition_When_MinutesAreTakenOneByOne()
    {
        Gen.Select(Rules, States, Minutes.Array[1, 40]).Sample((rule, state, minutes) =>
        {
            var (now, before) = (state, state);
            foreach (var minute in minutes)
            {
                (now, var transition) = AlertEvaluator.Step(rule, now, minute);
                (before, var expected) = Before.Step(rule, before, minute);
                now.Should().Be(before);
                transition.Should().Be(expected);
            }
        }, iter: Iter);
    }

    [Fact]
    public void Step_Should_GiveTheSameStateBack_When_AMinuteChangesNothing()
    {
        var rule = new AlertRuleValues("rule", ["A-VIS"], AlertMetric.Nowcast, AlertComparator.GreaterThan, 15, null, null, 3, 1, AlertSeverity.Warning, null, null,
            null, null, false, true);
        var (state, _) = AlertEvaluator.Step(rule, AlertTargetState.Fresh, new AlertMinute(T0, 20));

        AlertEvaluator.Step(rule, state, new AlertMinute(T0, 3)).State.Should().BeSameAs(state, "a minute already taken changes nothing, so nothing is allocated");
        AlertEvaluator.Step(rule, state, new AlertMinute(T0.AddMinutes(1), null)).State.Should()
            .Be(state with { LastMinuteUtc = T0.AddMinutes(1) }, "a minute without a value is taken: it moves the last minute only");
    }

    [Fact]
    public void Run_Should_AllocateOnlyItsTransitions_When_ALongSeriesInOrderIsFolded()
    {
        // A day in order with no transition: the fold allocates its result alone, not a state per minute (ARV-228b).
        var rule = new AlertRuleValues("rule", ["A-VIS"], AlertMetric.Nowcast, AlertComparator.GreaterThan, 15, null, null, 3, 1, AlertSeverity.Warning, null, null,
            null, null, false, true);
        var minutes = Enumerable.Range(0, 1440).Select(i => new AlertMinute(T0.AddMinutes(i), 4, 10)).ToList();
        AlertEvaluator.Run(rule, AlertTargetState.Fresh, minutes);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var (state, transitions) = AlertEvaluator.Run(rule, AlertTargetState.Fresh, minutes);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        transitions.Should().BeEmpty();
        state.LastMinuteUtc.Should().Be(T0.AddMinutes(1439));
        allocated.Should().BeLessThan(1024, "a day's fold in place allocates its list, its tuple and the state after the last minute only, about 200 bytes");
    }

    #endregion

    #region The predicted peak

    // Arrivals: mostly a few a minute, sometimes negative or not finite (a projection that has nothing to give).
    private static readonly Gen<double> Arrivals = Gen.Frequency(
        (20, Gen.Double[0, 12]),
        (1, Gen.OneOfConst(-1.0, double.NaN, double.PositiveInfinity)));

    [Fact]
    public void Peak_Should_GiveThePreviousPeak_When_AnyQueueThroughputAndWaveIsProjected()
    {
        Gen.Select(Gen.Int[-1, 60], Gen.Double[-1, 8].Nullable(), Arrivals.Array[0, 30], Gen.Int[0, 30]).Sample((queue, mu, wave, lead) =>
        {
            var expected = Before.Peak(queue, mu, wave, lead);
            var peak = PredictedWait.Peak(queue, mu, wave, lead);
            peak.Should().Be(expected);
        }, iter: Iter);
    }

    [Fact]
    public void Peak_Should_KeepTheFirstMinuteItIsReached_When_TheHighestNowcastRepeats()
    {
        // A queue of 4 at 2 a minute with arrivals of 2: the nowcast stays 2.5 minutes over the whole lead.
        PredictedWait.Peak(4, 2, [2, 2, 2, 2], 4).Should().Be(new PredictedPeak(2.5, 1), "the first minute the peak is reached is the one it is for");
    }

    #endregion

    #region A sensor rule's minutes

    // Outages within two hours of T0, some empty or reversed, some open (to the end of time), so spans overlap, touch and nest.
    private static readonly Gen<(DateTime From, DateTime To)> Outages = Gen.Select(Gen.Int[-30, 120], Gen.Int[-5, 40], Gen.Int[0, 9])
        .Select((from, length, open) => (T0.AddMinutes(from), open == 0 ? DateTime.MaxValue : T0.AddMinutes(from + length).AddSeconds(length % 3 * 20)));

    [Fact]
    public void OutageMinutes_Should_GiveThePreviousMinutes_When_TheOutagesOverlapTouchOrStayOpen()
    {
        Gen.Select(Outages.Array[0, 12], Gen.Int[0, 120]).Sample((outages, count) =>
        {
            var first = T0.AddMinutes(1);
            var expected = Before.OutageMinutes(first, count, outages);
            var minutes = new AlertInputs.OutageMinutes(first, count, outages);

            minutes.Count.Should().Be(count);
            minutes.Should().Equal(expected, "each minute is 1 inside an outage and 0 outside, as the list built a minute at a time had it");
            Enumerable.Range(0, count).Select(i => minutes[i]).Should().Equal(expected, "the index gives the minute the enumeration does");
        }, iter: Iter);
    }

    [Fact]
    public void OutageMinutes_Should_RefuseAnIndexOutsideTheRange_When_ItIsTaken()
    {
        var minutes = new AlertInputs.OutageMinutes(T0, 3, [(T0, T0.AddMinutes(1))]);

        minutes[0].Should().Be(new AlertMinute(T0, 1));
        minutes[2].Should().Be(new AlertMinute(T0.AddMinutes(2), 0));
        FluentActions.Invoking(() => minutes[3]).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => minutes[-1]).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new AlertInputs.OutageMinutes(T0, -1, [])).Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    /// <summary>The code before ARV-228b, word for word but for the minute being a value: the reference each change is held to.</summary>
    private static class Before
    {
        public static (AlertTargetState State, AlertTransition Transition) Step(AlertRuleValues rule, AlertTargetState state, AlertMinute minute)
        {
            ArgumentNullException.ThrowIfNull(rule);
            ArgumentNullException.ThrowIfNull(state);
            if (state.LastMinuteUtc is { } last && minute.MinuteUtc <= last)
                return (state, null);
            var next = state with { LastMinuteUtc = minute.MinuteUtc };
            if (minute.Value is not { } value || !double.IsFinite(value))
                return (next, null);

            var holds = AlertEvaluator.Holds(rule, value, minute.QueueLength);
            if (next.Armed)
            {
                if (!holds)
                    return (next with { Sustained = 0 }, null);
                var sustained = next.Sustained + 1;
                return sustained >= Math.Max(1, rule.SustainMinutes)
                    ? (next with { Armed = false, Sustained = 0, Clearing = 0 }, new AlertTransition(AlertTransitionKind.Raised, minute.MinuteUtc, value, minute.BinStartUtc, minute.PredictedForUtc))
                    : (next with { Sustained = sustained }, null);
            }

            if (!AlertEvaluator.Clears(rule, value, holds))
                return (next with { Clearing = 0 }, null);
            var clearing = next.Clearing + 1;
            return clearing >= Math.Max(1, rule.ClearAfterMinutes)
                ? (next with { Armed = true, Sustained = 0, Clearing = 0 }, new AlertTransition(AlertTransitionKind.Cleared, minute.MinuteUtc, value, minute.BinStartUtc, minute.PredictedForUtc))
                : (next with { Clearing = clearing }, null);
        }

        public static (AlertTargetState State, IReadOnlyList<AlertTransition> Transitions) Run(AlertRuleValues rule, AlertTargetState state, IEnumerable<AlertMinute> minutes)
        {
            var transitions = new List<AlertTransition>();
            foreach (var minute in minutes.OrderBy(m => m.MinuteUtc))
            {
                (state, var transition) = Step(rule, state, minute);
                if (transition is not null)
                    transitions.Add(transition);
            }

            return (state, transitions);
        }

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

        // AlertInputs.OutagesAsync's loop: every minute of the range, 1 when any outage holds it.
        public static List<AlertMinute> OutageMinutes(DateTime first, int count, IReadOnlyList<(DateTime From, DateTime To)> spans)
        {
            var list = new List<AlertMinute>();
            for (var m = first; m < first.AddMinutes(count); m = m.AddMinutes(1))
                list.Add(new AlertMinute(m, spans.Any(s => s.From <= m && m < s.To) ? 1 : 0));
            return list;
        }
    }
}
