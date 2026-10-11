using System.Collections.Concurrent;
using Ariva.Core.Border;
using Ariva.Core.Desks;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using Ariva.Core.Validation.Comparison;
using Ariva.Simulation.Api.Emulators.Aman;
using Ariva.Simulation.Api.Emulators.Validation;
using Ariva.Simulation.Api.Scenarios.Engine;
using Ariva.UnitTests.Replay;
using FluentAssertions;

namespace Ariva.UnitTests.Desks;

/// <summary>
/// ARV-149 on the reference evening (seed 9303, on which ARV-149 chose k and the timings) and, since ARV-151, on a held-out
/// evening (seed 2026, the same day drawn with another seed, never used to tune), measured on Ariva's own simulator, not on
/// field data: the Visitors desks
/// AR-08 to AR-22 with AMAN's sources alone (no staff or service zone, as at DMO in the ARV-104j rehearsal), fed as the desk
/// feed feeds them: the AMAN emulator's sessions and one-minute interval statistics through <see cref="AmanDeskFeed"/>, read
/// every 15 seconds, and once a minute, from the engine's watermark of the read, the lane context of
/// <see cref="DeskRhythm"/> from AMAN's intervals and the A-VIS zone's live minutes as the zone processor publishes them
/// (S-17's outage included). Each desk minute's dominant state (<see cref="DeskStateAgreement.Dominant"/>) is compared with
/// what an observer sees (the simulator's <see cref="ValidationTruth.Observed"/>, the rehearsal's truth; since ARV-151 a desk
/// serves when people wait or it works at least half the minute, as the AMAN emulator shares the people), with the lane
/// context (F10 rows 5a to 5f) and without it (rows 5 and 7, as before ARV-149), over the evening and by the scenario's
/// regime in the minute: a queue (people waiting), light demand (people arriving, nobody waiting) and the empty hall.
/// </summary>
public sealed class DeskRhythmScenarioTests(ITestOutputHelper output)
{
    /// <summary>F18's desk-state agreement target (the KPI annex's Proposed value).</summary>
    private const double Target = 0.95;

    // The evening compared (17:20 to 20:25, the engines' windows filled) and the rehearsal's window (18:00 to 19:30).
    private const int FirstMinute = 1040;
    private const int LastMinute = 1225;
    private const int RehearsalFrom = 1080;
    private const int RehearsalTo = 1170;

    /// <summary>The tuned evening (ARV-149) and the held-out evening (ARV-151).</summary>
    private const uint Tuned = ScenarioModel.DefaultSeed;
    private const uint HeldOut = 2026;

    private static readonly Ariva.Infra.Border.DeskFeedSettings Feed = new();

    private static readonly ConcurrentDictionary<uint, Lazy<ScenarioDay>> Days = new();

    private static readonly ConcurrentDictionary<uint, Lazy<List<(DateTime Available, QueueLiveMinute Minute)>>> Lives = new();

    private static readonly ConcurrentDictionary<(uint Seed, Contexts Mode), Lazy<List<DeskMinute>>> Runs = new();

    private static ScenarioDay DayOf(uint seed) =>
        Days.GetOrAdd(seed, s => new Lazy<ScenarioDay>(() => ScenarioDay.Run(ScenarioConfig.Reference(s) with { Lite = true }))).Value;

    private static List<(DateTime Available, QueueLiveMinute Minute)> LiveOf(uint seed) =>
        Lives.GetOrAdd(seed, s => new Lazy<List<(DateTime, QueueLiveMinute)>>(() => BuildLive(s))).Value;

    private static List<DeskMinute> MinutesOf(uint seed, Contexts mode) =>
        Runs.GetOrAdd((seed, mode), key => new Lazy<List<DeskMinute>>(() => Run(key.Seed, key.Mode))).Value;

    /// <summary>How the lane contexts are given: none (rows 5 and 7), as the desk feed gives them, and two comparisons.</summary>
    private enum Contexts
    {
        /// <summary>No lane context: the rows of before ARV-149.</summary>
        None,

        /// <summary>As built: from the engine's watermark of the read (the read's time less the feed's lateness).</summary>
        Watermark,

        /// <summary>The first design: from the whole minute of the read, so the queue it carries applies about 1.5 minutes later.</summary>
        ReadMinute,

        /// <summary>The scenario's own queue (whether people wait in the minute), with no lag and no measurement: a bound.</summary>
        TruthQueue
    }

    /// <summary>The A-VIS zone's live minutes of an evening, each with the receive time of the batch that published it.</summary>
    private static List<(DateTime, QueueLiveMinute)> BuildLive(uint seed)
    {
        var settings = new ZoneProcessorSettings();
        var zone = new ZoneProcessor(ZoneKeys.For(ReferenceReplay.Site, "A-VIS"), ReferenceReplay.GeometryOf("A-VIS"), ReferenceReplay.ProfileVersion, settings);
        var live = new List<(DateTime, QueueLiveMinute)>();
        var inputs = seed == Tuned ? ReferenceReplay.InputsOf("A-VIS") : ReferenceReplay.InputsOf("A-VIS", ReferenceReplay.IngestSeed(seed));
        foreach (var input in inputs)
        {
            var batch = input.ToBatch();
            zone.Offer(batch, ReferenceReplay.To);
            live.AddRange(zone.Drain().Live.Select(l => (batch.ReceivedUtc, l)));
        }

        return live;
    }

    /// <summary>The desk feed over an evening with the given lane contexts; the published engine's closed minutes.</summary>
    private static List<DeskMinute> Run(uint seed, Contexts mode)
    {
        var day = DayOf(seed);
        var q = ScenarioModel.Q("A-VIS");
        var desks = ReferenceReplay.VisitorDesks.ToDictionary(AmanCodes.Of, ReferenceReplay.DeskKeyOf, StringComparer.Ordinal);
        var profiles = desks.Values.Select(k => new DeskProfile(k, "VIS", HasTransactions: true, HasSession: true, HasStaffZone: false, HasServiceZone: false)).ToList();
        var settings = Feed.Engine;
        var engine = new DeskStateEngine(profiles, ReferenceReplay.From, settings);

        // AMAN publishes a minute's records when it completes; Ariva stores them a couple of seconds later.
        var records = new List<AmanFeedRecord>();
        var intervals = new List<(DateTime Received, DateTime Start, LaneIntervalSample Interval)>();
        for (var m = 1020; m < 1230; m++)
        {
            var received = ReferenceReplay.WallOf(m + 1).AddSeconds(2);
            var minute = AmanFeed.Build(day, m, k => ReferenceReplay.WallOf(k), ReferenceReplay.Site, BorderSides.Arrival, m == 1020);
            foreach (var s in minute.Sessions.Where(s => desks.ContainsKey(s.DeskCode)))
                records.Add(new AmanFeedRecord(AmanRecordKind.DeskSession, Guid.NewGuid(), received, desks[s.DeskCode], s.LaneCategory, s.OccurredAtUtc.UtcDateTime, s.State.ToString()));
            foreach (var d in minute.Desks.Where(d => desks.ContainsKey(d.DeskCode)))
            {
                records.Add(new AmanFeedRecord(AmanRecordKind.DeskInterval, Guid.NewGuid(), received, desks[d.DeskCode], d.LaneCategory, d.IntervalStartUtc.UtcDateTime,
                    Transactions: d.TransactionsProcessed, MeanCycleSeconds: d.MeanCycleSeconds));
                intervals.Add((received, d.IntervalStartUtc.UtcDateTime, new LaneIntervalSample("A-VIS", d.SourceEventId,
                    new DeskIntervalSample(d.TransactionsProcessed, d.DocumentsProcessed, d.MeanServiceSeconds, d.P90ServiceSeconds, d.MeanCycleSeconds))));
            }
        }

        var live = LiveOf(seed);
        var minutes = new List<DeskMinute>();
        DateTime? heartbeat = null, context = null;
        var read = DateTime.MinValue;
        for (var now = ReferenceReplay.From; now <= ReferenceReplay.To.AddMinutes(5); now = now.AddSeconds(Feed.PollSeconds))
        {
            var fresh = records.Where(r => r.ReceivedUtc > read && r.ReceivedUtc <= now).ToList();
            read = now;
            var step = AmanDeskFeed.Step(fresh, [.. desks.Values], heartbeat, now);
            heartbeat = step.HeartbeatUtc;
            foreach (var signal in step.Signals)
                engine.Offer(signal, now);

            // Once a whole minute, as DeskFeed.LaneContextsAsync reads them: what is stored by now, from the moment they apply.
            var minuteOfRead = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
            if (mode != Contexts.None && (!context.HasValue || minuteOfRead > context.Value))
            {
                var at = mode == Contexts.ReadMinute ? minuteOfRead : now - settings.Lateness;
                var queues = live.Where(l => l.Available <= now).Select(l => new LaneQueueSample("A-VIS", l.Minute.MinuteUtc, l.Minute.QueueLength, l.Minute.LengthDegraded));
                var window = intervals.Where(i => i.Received <= now && i.Start >= at.AddMinutes(-Feed.RhythmWindowMinutes - 2) && i.Start <= at).Select(i => i.Interval);
                var truthIndex = (int)Math.Floor((at - ReferenceReplay.Midnight).TotalMinutes) + ScenarioModel.Pre;
                foreach (var c in DeskRhythm.Contexts(desks.Values.Select(k => (k, "A-VIS")), queues, window, at).Contexts)
                    engine.Offer(mode == Contexts.TruthQueue ? c with { Waiting = day.L[q][truthIndex] > 0.5 ? 1 : 0, Degraded = false } : c, now);
                context = minuteOfRead;
            }

            minutes.AddRange(engine.Advance(now).Minutes);
        }

        engine.Counters.Should().BeEquivalentTo(new { Invalid = 0L, Future = 0L, BufferFull = 0L, UnknownDesk = 0L });
        if (mode == Contexts.Watermark)
            engine.Counters.Superseded.Should().Be(0, "one context per desk and minute, in time order");
        return minutes;
    }

    private sealed record Agreement(int Judged, int Agreeing, int OpenAgreeing, IReadOnlyDictionary<(DeskStatus Observed, DeskStatus System), int> Confusion,
        IReadOnlyDictionary<string, (int Judged, int Agreeing)> Regimes)
    {
        public double Share => (double)Agreeing / Judged;

        public double OpenShare => (double)OpenAgreeing / Judged;

        public double In(string regime) => (double)Regimes[regime].Agreeing / Regimes[regime].Judged;
    }

    /// <summary>Each Visitors desk minute of [from, to) of an evening against what an observer sees in it, in all and by regime.</summary>
    private static Agreement Compare(uint seed, IReadOnlyList<DeskMinute> minutes, int from, int to)
    {
        var day = DayOf(seed);
        var q = ScenarioModel.Q("A-VIS");
        var servers = ScenarioModel.Queues[q].Servers;
        var byKey = minutes.ToDictionary(m => (m.DeskCode, m.MinuteUtc));
        var confusion = new Dictionary<(DeskStatus, DeskStatus), int>();
        var regimes = new Dictionary<string, (int Judged, int Agreeing)>(StringComparer.Ordinal);
        int judged = 0, agreeing = 0, openAgreeing = 0;
        for (var minute = from; minute < to; minute++)
        {
            var i = minute + ScenarioModel.Pre;
            var states = day.ServerStates(q, minute);
            var regime = day.L[q][i] > 0.5 ? "queue" : day.D[q][i] > 0.01 ? "light demand" : "empty hall";
            for (var k = 0; k < servers.Count; k++)
            {
                var observed = Observed(ValidationTruth.Observed(states[k].State, day.Servers[q][k].Pause[i] != 0, ValidationTruth.Serves(day, q, minute, k)));
                var stored = byKey[(ReferenceReplay.DeskKeyOf(servers[k]), ReferenceReplay.WallOf(minute))];
                var system = DeskStateAgreement.Dominant(stored.Closed.TotalSeconds, stored.Idle.TotalSeconds, stored.Serving.TotalSeconds, stored.Paused.TotalSeconds,
                    stored.Unknown.TotalSeconds) ?? DeskStatus.Unknown;
                var agrees = system != DeskStatus.Unknown && system == observed;
                judged++;
                agreeing += agrees ? 1 : 0;
                openAgreeing += DeskStateAgreement.CountsForThroughput(system) == DeskStateAgreement.CountsForThroughput(observed) && system != DeskStatus.Unknown ? 1 : 0;
                confusion[(observed, system)] = confusion.GetValueOrDefault((observed, system)) + 1;
                var tally = regimes.GetValueOrDefault(regime);
                regimes[regime] = (tally.Judged + 1, tally.Agreeing + (agrees ? 1 : 0));
            }
        }

        return new Agreement(judged, agreeing, openAgreeing, confusion, regimes);
    }

    private static DeskStatus Observed(ObservedDeskState state) => state switch
    {
        ObservedDeskState.Serving => DeskStatus.Serving,
        ObservedDeskState.Idle => DeskStatus.Idle,
        ObservedDeskState.Paused => DeskStatus.Paused,
        _ => DeskStatus.Closed
    };

    private void Print(string name, Agreement a)
    {
        output.WriteLine($"{name}: {a.Agreeing} of {a.Judged} desk minutes agree ({a.Share:F3}); open or not open {a.OpenShare:F3}");
        foreach (var (regime, (judged, agreeing)) in a.Regimes.OrderBy(r => r.Key, StringComparer.Ordinal))
            output.WriteLine($"  {regime}: {agreeing} of {judged} ({(double)agreeing / judged:F3})");
        foreach (var ((observed, system), count) in a.Confusion.OrderByDescending(c => c.Value))
            output.WriteLine($"  observed {observed}, system {system}: {count}");
    }

    [Theory]
    [InlineData(Tuned)]
    [InlineData(HeldOut)]
    public void Engine_Should_AgreeWithTheObservers_When_DesksWithoutZonesFollowTheTransactionRhythm(uint seed)
    {
        var withRhythm = MinutesOf(seed, Contexts.Watermark);
        var evening = Compare(seed, withRhythm, FirstMinute, LastMinute);
        var rehearsal = Compare(seed, withRhythm, RehearsalFrom, RehearsalTo);
        Print($"Seed {seed}, with the transaction rhythm, 17:20 to 20:25", evening);
        Print($"Seed {seed}, with the transaction rhythm, 18:00 to 19:30", rehearsal);

        // The same bounds on the tuned and the held-out evening (measured, docs/domain/formulas.md F10, ARV-151). With a
        // queue every logged-in desk serves and the target is met (0.989 and 0.986).
        evening.In("queue").Should().BeGreaterThanOrEqualTo(Target);
        // In the empty hall a desk stays Serving for s + I after its last transaction and the queue reaches the context a
        // minute or two late (0.925 and 0.909).
        evening.In("empty hall").Should().BeGreaterThanOrEqualTo(0.9);
        // In light demand (people arriving, nobody waiting) the truth since ARV-151 shares the served people among the working
        // desks as the AMAN emulator does, and a desk serves when it works at least half the minute; the rhythm reads a desk
        // Serving for s + I after each transaction however little of the minute it works (0.676 on both evenings).
        evening.In("light demand").Should().BeGreaterThanOrEqualTo(0.65);
        // Over all minutes (0.914 and 0.909; 18:00 to 19:30, 0.909 and 0.907); open or not open is what the nowcast counts
        // (0.996 and 0.992).
        evening.Share.Should().BeGreaterThanOrEqualTo(0.9);
        rehearsal.Share.Should().BeGreaterThanOrEqualTo(0.9);
        evening.OpenShare.Should().BeGreaterThanOrEqualTo(0.99);
    }

    [Theory]
    [InlineData(Tuned)]
    [InlineData(HeldOut)]
    public void Engine_Should_DisagreeAsInTheRehearsal_When_TheLaneContextIsLeftOut(uint seed)
    {
        // F2 of the ARV-104j rehearsal reproduced: AMAN alone never makes a desk Serving and pauses it T1 after a transaction.
        var withoutRhythm = MinutesOf(seed, Contexts.None);
        var evening = Compare(seed, withoutRhythm, FirstMinute, LastMinute);
        var rehearsal = Compare(seed, withoutRhythm, RehearsalFrom, RehearsalTo);
        Print($"Seed {seed}, without the lane context (rows 5 and 7), 17:20 to 20:25", evening);
        Print($"Seed {seed}, without the lane context (rows 5 and 7), 18:00 to 19:30", rehearsal);

        evening.Confusion.Where(c => c.Key.System == DeskStatus.Serving).Sum(c => c.Value).Should().Be(0, "no transaction starts: never Serving");
        rehearsal.Share.Should().BeLessThan(0.5);
    }

    [Theory]
    [InlineData(Tuned)]
    [InlineData(HeldOut)]
    public void Engine_Should_LoseLittleToTheContextsLag_When_TheyApplyFromTheWatermark(uint seed)
    {
        // The lane context carries the zones' latest live minutes, a minute or two behind the desk engine's watermark: applied
        // from the watermark it agrees within a hundredth of the scenario's own queue without lag, and better than applied
        // from the minute of the read (the first design).
        var built = Compare(seed, MinutesOf(seed, Contexts.Watermark), FirstMinute, LastMinute);
        var readMinute = Compare(seed, MinutesOf(seed, Contexts.ReadMinute), FirstMinute, LastMinute);
        var truth = Compare(seed, MinutesOf(seed, Contexts.TruthQueue), FirstMinute, LastMinute);
        Print($"Seed {seed}, contexts from the minute of the read, 17:20 to 20:25", readMinute);
        Print($"Seed {seed}, the scenario's own queue without lag, 17:20 to 20:25", truth);

        built.Share.Should().BeGreaterThan(readMinute.Share + 0.01);
        built.Share.Should().BeGreaterThanOrEqualTo(truth.Share - 0.01);
    }
}
