using System.Text.Json;
using Ariva.Core.Desks;
using Ariva.Core.Queueing;
using Ariva.Core.Queueing.Replay;
using Ariva.Core.Sensing;
using Ariva.Simulation.Api.Emulators.Aman;
using Ariva.Simulation.Api.Scenarios.Engine;
using Ariva.UnitTests.Replay;
using FluentAssertions;

namespace Ariva.UnitTests.Desks;

/// <summary>
/// ARV-117b on the reference evening (seed 9303), measured on Ariva's own simulator, not on field data: the Visitors
/// desks AR-08 to AR-22 all have AMAN codes and AMAN is live all evening (sessions with a heartbeat each minute and the
/// emulator's one-minute interval statistics), while S-18 to S-20 report their staff and service zones (the ARV-116 and
/// ARV-117a desk zone case). The A-VIS zone processor runs as the stream does, taking every 15 seconds the desk term
/// <see cref="DeskTerms.Compute"/> gives from the desk minutes closed by then (published engine with AMAN, sensor-only
/// engine with the zones alone) and AMAN's interval statistics, as <c>DeskTermSource</c> reads them. The published nowcast
/// takes AMAN's n_open and lane cycle time; the shadow takes the zones' n_open over the sensor cycle time. Each is
/// compared with the scenario's true wait: the realised mean wait of the people who joined in the minute after the
/// nowcast's minute (the minute in which it is published).
/// </summary>
public sealed class SensorCycleScenarioTests(ITestOutputHelper output)
{
    private static readonly Lazy<ScenarioDay> Day = new(() => ScenarioDay.Run(ScenarioConfig.Reference() with { Lite = true }));

    private static readonly DeskStateSettings FeedSettings = new Ariva.Infra.Border.DeskFeedSettings().Engine;

    private static readonly ZoneProcessorSettings Settings = new();

    // The evening compared: from 17:20 (the engines and the windows have filled) to 20:25 (before the replay's end).
    private const int FirstMinute = 1040;
    private const int LastMinute = 1225;

    /// <summary>A closed desk minute of both engines with the wall time it closed at (both are written in one transaction).</summary>
    private sealed record Closed(DateTime At, DeskMinuteSample Sample);

    private static readonly Lazy<(List<Closed> Minutes, List<(DateTime Start, DateTime Published, double Cycle, int Transactions)> Intervals)> Desks = new(BuildDesks);

    private static (List<Closed>, List<(DateTime, DateTime, double, int)>) BuildDesks()
    {
        var day = Day.Value;
        var q = ScenarioModel.Q("A-VIS");
        var keys = ReferenceReplay.VisitorDesks.ToDictionary(d => d, ReferenceReplay.DeskKeyOf, StringComparer.Ordinal);
        var profiles = keys.Values.Select(k => new DeskProfile(k, "VIS", HasTransactions: false, HasSession: true, HasStaffZone: true, HasServiceZone: true)).ToList();
        var published = new DeskStateEngine(profiles, ReferenceReplay.From, FeedSettings);
        var sensorOnly = SensorOnlyDeskEngine.Start(profiles, ReferenceReplay.From, new Ariva.Infra.Border.DeskFeedSettings().SensorEngine);
        var readings = ReferenceReplay.RunDesks().Outputs["DMO/A-VIS"][0].DeskReadings;
        var byArrival = readings.Select(r => (Arrival: r.TimeUtc.AddMinutes(1), Reading: r)).OrderBy(r => r.Arrival).ToList();
        var last = new Dictionary<string, DeskSessionSignal>(StringComparer.Ordinal);
        var pendingPublished = new Dictionary<(string, DateTime), (DateTime At, DeskMinute Minute)>();
        var pendingSensor = new Dictionary<(string, DateTime), (DateTime At, DeskMinute Minute)>();
        var next = 0;
        for (var now = ReferenceReplay.From; now <= ReferenceReplay.To.AddMinutes(5); now = now.AddSeconds(15))
        {
            if (now.Second == 0 && now < ReferenceReplay.To)
            {
                var minute = (int)(now - ReferenceReplay.WallOf(0)).TotalMinutes;
                foreach (var server in day.ServerStates(q, minute).Where(s => s.State != "unknown"))
                {
                    var session = server.State switch { "closed" or "oos" => DeskSessionSignal.Closed, "paused" => DeskSessionSignal.Paused, _ => DeskSessionSignal.Opened };
                    DeskSignal signal = last.TryGetValue(server.Id, out var previous) && previous == session
                        ? new DeskHeartbeat(keys[server.Id], now, DeskSource.Session)
                        : new DeskSessionChangedSignal(keys[server.Id], now, session);
                    published.Offer(signal, now);
                    last[server.Id] = session;
                }
            }

            while (next < byArrival.Count && byArrival[next].Arrival <= now)
            {
                var reading = byArrival[next++].Reading.ToSignal();
                published.Offer(reading, now);
                sensorOnly.Offer(reading, now);
            }

            foreach (var m in published.Advance(now).Minutes)
                pendingPublished[(m.DeskCode, m.MinuteUtc)] = (now, m);
            foreach (var m in sensorOnly.Advance(now).Minutes)
                pendingSensor[(m.DeskCode, m.MinuteUtc)] = (now, m);
        }

        // The desk feed writes both engines' minutes in one transaction: a minute is readable once both have closed it.
        var minutes = pendingPublished.Select(p =>
        {
            var (at, m) = p.Value;
            var sensor = pendingSensor.TryGetValue(p.Key, out var s) ? s : default;
            var sample = new DeskMinuteSample(m.DeskCode, m.MinuteUtc, m.Idle.TotalSeconds, m.Serving.TotalSeconds, m.Unknown.TotalSeconds, m.Transactions, m.Degraded,
                m.SensorDerived.TotalSeconds,
                sensor.Minute is { } sm ? new DeskSensorSample(sm.Open.TotalSeconds, sm.Unknown.TotalSeconds, sm.Degraded, sm.Serving.TotalSeconds) : null);
            return new Closed(sensor.Minute is null ? at : (at > sensor.At ? at : sensor.At), sample);
        }).OrderBy(c => c.At).ToList();

        // AMAN's one-minute interval statistics of the Visitors desks, received when the minute completes.
        var codes = ReferenceReplay.VisitorDesks.Select(AmanCodes.Of).ToHashSet(StringComparer.Ordinal);
        var intervals = new List<(DateTime, DateTime, double, int)>();
        for (var m = 1020; m < 1230; m++)
        {
            foreach (var d in AmanFeed.Build(day, m, k => ReferenceReplay.WallOf(k), ReferenceReplay.Site, BorderSides.Arrival, m == 1020).Desks
                         .Where(d => codes.Contains(d.DeskCode)))
                intervals.Add((d.IntervalStartUtc.UtcDateTime, ReferenceReplay.WallOf(m + 1), d.MeanCycleSeconds, d.TransactionsProcessed));
        }

        return (minutes, intervals);
    }

    /// <summary>The desk term the stream would read at <paramref name="now"/>, as DeskTermSource builds it.</summary>
    private static DeskTerm TermAt(DateTime now, Func<DeskMinuteSample, DeskMinuteSample> change = null)
    {
        var (minutes, intervals) = Desks.Value;
        var samples = minutes.Where(c => c.At <= now && c.Sample.MinuteUtc >= now.AddMinutes(-Ariva.Infra.Streaming.DeskTermSource.LookbackMinutes) && c.Sample.MinuteUtc <= now)
            .Select(c => change is null ? c.Sample : change(c.Sample)).ToList();
        var cycle = LaneCycle.Minutes(intervals.Where(i => i.Published <= now && i.Start >= now.AddMinutes(-Settings.ExitWindowMinutes - 2) && i.Start <= now)
            .Select(i => (i.Cycle, i.Transactions)));
        return DeskTerms.Compute(samples, Settings.ExitWindowMinutes, now, cycle, Settings.SensorCycle);
    }

    /// <summary>
    /// The A-VIS zone processor over the evening's inputs with the desk term refreshed every 15 seconds of receive time;
    /// <paramref name="term"/> maps a wall time to the term the zone takes.
    /// </summary>
    private static List<QueueLiveMinute> Evening(Func<DateTime, DeskTerm> term)
    {
        var zone = new ZoneProcessor(ZoneKeys.For(ReferenceReplay.Site, "A-VIS"), ReferenceReplay.DeskGeometryOf("A-VIS"), ReferenceReplay.ProfileVersion, Settings);
        var live = new List<QueueLiveMinute>();
        var refresh = DateTime.MinValue;
        foreach (var input in ReferenceReplay.InputsOf("A-VIS", ReferenceReplay.DeskIngested))
        {
            var batch = input.ToBatch();
            if (batch.ReceivedUtc >= refresh)
            {
                zone.UseDesks(term(batch.ReceivedUtc));
                refresh = batch.ReceivedUtc.AddSeconds(15);
            }

            zone.Offer(batch, ReferenceReplay.To);
            live.AddRange(zone.Drain().Live);
        }

        zone.Finish(ReferenceReplay.To, ReferenceReplay.To + ZoneReplay.Settle(Settings));
        live.AddRange(zone.Drain().Live);
        return live;
    }

    private static readonly Lazy<List<QueueLiveMinute>> WithCycle = new(() => Evening(now => TermAt(now)));

    // ARV-117a: the same terms without the sensor cycle time (no busy window): the shadow's c falls back.
    private static readonly Lazy<List<QueueLiveMinute>> WithoutCycle = new(() => Evening(now => TermAt(now) is { SensorOnly: { } s } t ? t with { SensorOnly = s with { SensorBusy = null } } : TermAt(now)));

    // The estimator first offered (open desk minutes over exits): the same formula with every open second counted as busy.
    private static readonly Lazy<List<QueueLiveMinute>> OpenTimeCycle = new(() => Evening(now => TermAt(now, m =>
        m.Sensor is { } s ? m with { Sensor = s with { ServingSeconds = s.OpenSeconds } } : m)));

    private sealed record Score(string Name, int Minutes, double Mae, double Bias, int NoNumber);

    private static Score Measure(string name, IEnumerable<QueueLiveMinute> live, Func<QueueLiveMinute, double?> wait)
    {
        var day = Day.Value;
        var q = ScenarioModel.Q("A-VIS");
        var pairs = new List<(double Estimate, double Truth)>();
        var noNumber = 0;
        foreach (var l in live.Where(l => l.MinuteUtc >= ReferenceReplay.WallOf(FirstMinute) && l.MinuteUtc <= ReferenceReplay.WallOf(LastMinute)))
        {
            var minute = (int)(l.MinuteUtc - ReferenceReplay.Midnight).TotalMinutes;
            var truth = day.MeanWait[q][minute + 1 + ScenarioModel.Pre];
            if (double.IsNaN(truth))
                continue;
            if (wait(l) is { } w)
                pairs.Add((w, truth));
            else
                noNumber++;
        }

        return new Score(name, pairs.Count, pairs.Average(p => Math.Abs(p.Estimate - p.Truth)), pairs.Average(p => p.Estimate - p.Truth), noNumber);
    }

    [Fact]
    public void Published_Should_BeIdentical_When_TheSensorCycleTimeIsPresentOrAbsent()
    {
        // The published rows (every value, reason and flag, serialised as the live snapshot and the ledger take them; the
        // shadow is never serialised) are the same with the sensor cycle time, without it, and with the other estimator.
        var with = WithCycle.Value;
        var without = WithoutCycle.Value;
        var openTime = OpenTimeCycle.Value;

        with.Should().HaveCountGreaterThan(200);
        JsonSerializer.Serialize(with, ReplayLedger.Json).Should().Be(JsonSerializer.Serialize(without, ReplayLedger.Json));
        JsonSerializer.Serialize(with, ReplayLedger.Json).Should().Be(JsonSerializer.Serialize(openTime, ReplayLedger.Json));
        with.Select(l => (l.NowcastMinutes, l.Throughput, l.NoService, l.NowcastDegraded, l.QueueLength, l.LengthDegraded))
            .Should().Equal(without.Select(l => (l.NowcastMinutes, l.Throughput, l.NoService, l.NowcastDegraded, l.QueueLength, l.LengthDegraded)));
        with.Select(l => Ariva.Infra.Live.LiveZoneSnapshot.From(l, l.MinuteUtc.AddMinutes(1))).Should().BeEquivalentTo(
            without.Select(l => Ariva.Infra.Live.LiveZoneSnapshot.From(l, l.MinuteUtc.AddMinutes(1))), o => o.WithStrictOrdering(), "the live snapshot (screens, displays, alerts) is unchanged");

        // Only the shadow moves, and the same input gives the same shadow every run.
        with.Zip(without).Count(p => p.First.Shadow != p.Second.Shadow).Should().BeGreaterThan(50);
        Evening(now => TermAt(now)).Select(l => l.Shadow).Should().Equal(with.Select(l => l.Shadow), "deterministic");
    }

    [Fact]
    public void Shadow_Should_TakeTheZonesNOpenOverTheSensorCycleTime_When_AmanIsLiveForEveryDesk()
    {
        var with = WithCycle.Value;
        var evening = with.Where(l => l.MinuteUtc >= ReferenceReplay.WallOf(FirstMinute) && l.MinuteUtc <= ReferenceReplay.WallOf(LastMinute)).ToList();
        var used = evening.Count(l => l.Shadow.CycleMinutes is not null);
        var published = Measure("published (AMAN n_open and lane cycle time)", with, l => l.NowcastMinutes);
        var shadow = Measure("shadow, zones' n_open over the sensor cycle time (ARV-117b)", with, l => l.Shadow.Minutes);
        var before = Measure("shadow without a cycle time (ARV-117a: exit term alone)", WithoutCycle.Value, l => l.Shadow.Minutes);
        var openTime = Measure("shadow, open desk minutes over exits (rejected)", OpenTimeCycle.Value, l => l.Shadow.Minutes);
        var cycles = evening.Where(l => l.Shadow.CycleMinutes is not null).Select(l => l.Shadow.CycleMinutes!.Value).Order().ToList();

        output.WriteLine($"Reference evening 17:20 to 20:25 ({evening.Count} minutes), measured on Ariva's own simulator (seed 9303), not on field data.");
        output.WriteLine($"Shadow minutes with the sensor cycle time: {used} of {evening.Count}; c from {cycles[0]:F2} to {cycles[^1]:F2} min, median {cycles[cycles.Count / 2]:F2}; " +
                         $"the scenario's Visitors service time is {ScenarioModel.Svc["VIS"] / 60:F2} min per desk at speed factor 1.");
        var aman = Enumerable.Range(FirstMinute, LastMinute - FirstMinute + 1).Select(m => TermAt(ReferenceReplay.WallOf(m + 1).AddSeconds(30))?.CycleMinutes)
            .OfType<double>().Order().ToList();
        output.WriteLine($"AMAN's lane cycle time as the published term reads it (the emulator's interval statistics): median {aman[aman.Count / 2]:F2} min, " +
                         $"from {aman[0]:F2} to {aman[^1]:F2}.");
        foreach (var s in new[] { published, shadow, before, openTime })
            output.WriteLine($"{s.Name}: {s.Minutes} minutes with a true wait and a number, mean absolute error {s.Mae:F2} min, bias {s.Bias:+0.00;-0.00} min, {s.NoNumber} without a number");

        used.Should().BeGreaterThan(evening.Count / 2, "the sensor cycle time is available most of the evening");
        cycles.Should().OnlyContain(c => c >= Settings.SensorCycle.MinimumCycleMinutes && c <= Settings.SensorCycle.MaximumCycleMinutes);
        shadow.Mae.Should().BeLessThanOrEqualTo(before.Mae, "on the simulator the sensor cycle time does not make the shadow worse than the exit term alone");
        openTime.Mae.Should().BeGreaterThan(shadow.Mae, "open time over exits carries the idle time of a short queue");

        // The shadow's term at an all-AMAN site with AMAN live: n_open from the zones over the sensor c, while the published
        // term keeps AMAN's n_open and lane cycle time.
        var at = ReferenceReplay.WallOf(1085).AddSeconds(30);
        var term = TermAt(at);
        term.CycleMinutes.Should().NotBeNull("AMAN's lane cycle time");
        term.SensorOnly.SensorBusy.Missing.Should().BeNull();
        term.SensorOnly.CycleMinutes.Should().BeNull("the term as read has the busy window, the zone gives it c with its exits");
    }
}
