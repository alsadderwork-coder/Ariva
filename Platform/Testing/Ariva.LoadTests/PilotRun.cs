using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Ariva.LoadTests;

/// <summary>
/// ARV-124a, the pilot hall run (ARIVA_LOAD_MODE=pilot), against hosts that include Ariva.Api.Stream: the screens join every
/// zone and the displays poll while AMAN's feed runs, then
/// <list type="number">
/// <item>steady: the pilot's 30 sensors at a push a second (4,500 positions a second);</item>
/// <item>burst: every sensor of the site (100) at a push a second, the D5 site rate of 15,000 positions a second, with one
/// device flooding past its limit and bodies over 256 KB from a measured device during it;</item>
/// <item>recovery: the pilot's sensors again until Stream's lag is back to its steady value (or the window ends).</item>
/// </list>
/// Measured: every answer (never a 5xx; 413 and 429 with Retry-After where the limits apply; no 429 for a measured device),
/// Stream's lag and catch-up from the broker, the snapshots Stream announced to each zone's screens, the boards, the hosts'
/// CPU and memory. Two verdicts: problems (the request path, the limits, the screens, the boards, AMAN's feed) and capacity
/// (Stream ran the whole run, and its queue engine and archive caught up after the burst). Writes pilot-report.json and
/// pilot-report.md to ARIVA_LOAD_OUTPUT; exits 1 on a problem, 2 on capacity alone, 0 when both are clean.
/// </summary>
public static class PilotRun
{
    private const string QueueEngine = "ariva-stream.queue-engine";
    /// <summary>A device's limit at Ingest in this run: Security:RateLimiting:Device, 600 a minute (the production default).</summary>
    private const int FloodLimit = 600;
    private const string Archive = "ariva-stream.sensing-archive-tracks";

    public static async Task<int> RunAsync()
    {
        // The flood rule judges what the run may excuse: prove it on its table before anything is sent.
        FloodRule.SelfTest();
        var settings = PilotSettings.FromEnvironment();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(settings.SteadySeconds + settings.BurstSeconds + settings.RecoverySeconds + 300));
        var ingest = ArivaApis.Ingest(settings.Ingest);
        var site = settings.Site;
        var zones = site.Zones.ToDictionary(z => z.Name, StringComparer.Ordinal);
        var pilot = site.Devices.Where(d => !d.Burst).ToList();
        var every = site.Devices.ToList();
        var packages = new long[every.Count + 1];
        // Each device's previous push: its crossings since then go in its next one.
        var lastPush = new long[every.Count + 1];
        var sentIn = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        var sentOut = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        string firstRejection = null;
        var started = DateTime.UtcNow;

        Console.WriteLine(LoadSettings.Invariant($"ARV-124a pilot run: {zones.Count} zones, {pilot.Count} sensors ({every.Count} in the burst), {settings.Screens} screens, {settings.DisplayKeys.Count} displays, {site.Desks.Count} desks and {site.Gates.Count} e-gates on AMAN's feed."));

        async Task PushAsync(Measure measure, PilotDevice device, int index, int padding, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var previous = padding > 0 ? now.Ticks : Interlocked.Exchange(ref lastPush[index], now.Ticks);
            var since = previous == 0 ? now.AddSeconds(-1) : new DateTime(previous, DateTimeKind.Utc);
            var push = PilotTracks.Body(zones[device.Zone], index, Interlocked.Increment(ref packages[index]), since, now, padding > 0 ? 1 : settings.PeoplePerDevice,
                padding > 0 ? 1 : settings.SamplesPerPerson, settings.DwellSeconds, padding);
            var body = push.Body;
            measure.Begin();
            var clock = Stopwatch.StartNew();
            try
            {
                using var response = await ingest.PushAsync(device.Zone, new MemoryStream(body, writable: false), device.Key, ct);
                measure.Record(clock.Elapsed.TotalMilliseconds, response.StatusCode, body.Length);
                if (response.StatusCode == HttpStatusCode.Accepted)
                {
                    sentIn.AddOrUpdate(device.Zone, push.Entries, (_, n) => n + push.Entries);
                    sentOut.AddOrUpdate(device.Zone, push.Exits, (_, n) => n + push.Exits);
                    // Ingest refuses a bad event on its own and still answers 202: a refusal here is a harness or an Ingest problem,
                    // and so is an answer the harness cannot read.
                    try
                    {
                        using var answer = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                        if (answer.RootElement.TryGetProperty("rejected", out var rejected) && rejected.ValueKind == JsonValueKind.Number && rejected.GetInt32() > 0)
                        {
                            measure.Record(0, "202 with rejected events");
                            if (answer.RootElement.TryGetProperty("problems", out var why) && why.ValueKind == JsonValueKind.Array && why.GetArrayLength() > 0 &&
                                why[0].ValueKind == JsonValueKind.String)
                                Interlocked.CompareExchange(ref firstRejection, why[0].GetString(), null);
                        }
                    }
                    catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
                    {
                        measure.Record(0, "202 with rejected events");
                    }
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests && response.Headers.RetryAfter is null)
                    measure.Record(0, "429 without Retry-After");
                if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge && response.Headers.ConnectionClose != true)
                    measure.Record(0, "413 without Connection: close");
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Whatever went wrong is the push's outcome, never lost (only the run's own end stops a push without one); the
                // kind of failure only (a message can carry the address).
                measure.Record(clock.Elapsed.TotalMilliseconds, "error " + e.GetType().Name + (e is HttpRequestException h ? " " + h.HttpRequestError : ""));
            }
        }

        async Task<Measure> PushesAsync(string name, IReadOnlyList<PilotDevice> devices, int seconds, CancellationToken ct)
        {
            var measure = new Measure(name);
            measure.Start();
            await Inject.RunAsync(devices.Count, seconds, n =>
            {
                var device = devices[(int)(n % devices.Count)];
                return PushAsync(measure, device, every.IndexOf(device), 0, ct);
            }, ct);
            measure.Stop();
            return measure;
        }

        Measure steady, burst, flood, oversize, aman;
        var recovery = new Measure("recovery pushes");
        LiveFanout.Result live;
        object hosts;
        (long Steady, long Peak, double? CatchUpSeconds) engine, archive;
        IReadOnlyList<StreamLag.Sample> lagSamples;
        IReadOnlyDictionary<string, int> partitions;
        int lagErrors, streamSeconds, mainSeconds;
        var recoverySeconds = 0;
        await using (var sampler = new HostSampler())
        await using (var lag = new StreamLag(settings.KafkaBootstrap))
        using (var feed = new AmanFeed(settings))
        {
            lag.Start();
            using var endLive = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
            var fanout = new LiveFanout(settings.Fanout(), ArivaApis.Display(settings.Main));
            var liveTask = fanout.RunAsync(endLive.Token);
            var amanTask = feed.RunAsync(endLive.Token);
            // The screens come up first.
            await Task.Delay(TimeSpan.FromSeconds(5), cancel.Token);

            lag.Phase = "steady";
            steady = await PushesAsync("steady pushes (pilot hall)", pilot, settings.SteadySeconds, cancel.Token);

            lag.Phase = "burst";
            var burstTask = PushesAsync("burst pushes (D5 site rate)", every, settings.BurstSeconds, cancel.Token);
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, settings.BurstSeconds / 3)), cancel.Token);
            var floodTask = FloodAsync();
            oversize = new Measure("oversize (over 256 KB)");
            oversize.Start();
            await Inject.RunAsync(5, 4, _ => PushAsync(oversize, pilot[0], every.IndexOf(pilot[0]), 256 * 1024 + 10, cancel.Token), cancel.Token);
            oversize.Stop();
            flood = await floodTask;
            burst = await burstTask;

            // The pilot's sensors again, ten seconds at a time, until Stream's lag is back (at least a minute, at most the window).
            lag.Phase = "recovery";
            recovery.Start();
            var window = Stopwatch.StartNew();
            while (window.Elapsed < TimeSpan.FromSeconds(settings.RecoverySeconds))
            {
                recovery.Add(await PushesAsync("recovery pushes", pilot, 10, cancel.Token));
                recoverySeconds += 10;
                if (window.Elapsed >= TimeSpan.FromSeconds(60) && lag.CatchUp(QueueEngine, "steady", "burst").CatchUpSeconds is not null &&
                    lag.CatchUp(Archive, "steady", "burst").CatchUpSeconds is not null)
                    break;
            }

            recovery.Stop();
            lag.Phase = "end";
            await endLive.CancelAsync();
            live = await liveTask;
            aman = await amanTask;
            hosts = sampler.Summary();
            streamSeconds = sampler.SamplesOf("Ariva.Api.Stream");
            mainSeconds = sampler.SamplesOf("Ariva.Api.Main");
            engine = lag.CatchUp(QueueEngine, "steady", "burst");
            archive = lag.CatchUp(Archive, "steady", "burst");
            lagSamples = lag.Samples;
            partitions = lag.Partitions;
            lagErrors = lag.Errors;

            async Task<Measure> FloodAsync()
            {
                var measure = new Measure("flood (one device past its limit)");
                measure.Start();
                await Inject.RunAsync(settings.FloodPerSecond, settings.FloodSeconds, _ => PushAsync(measure, site.Flood, every.Count, 0, cancel.Token), cancel.Token);
                measure.Stop();
                return measure;
            }
        }

        var problems = new List<string>();
        var capacity = new List<string>();
        // The flooding device: see FloodRule.
        if (FloodRule.Judge(flood.Started, FloodLimit, Outcomes(flood)) is { } floodProblem)
            problems.Add(floodProblem);
        foreach (var m in new[] { steady, burst, recovery, oversize, live.Connects, live.Boards, aman })
        {
            if (m.Failures > 0)
                problems.Add(LoadSettings.Invariant($"{m.Name}: {m.Failures} failed (5xx, transport or broker)."));
        }

        // One outcome for each push started (the marks beside a 202 or a 429 are not outcomes of their own).
        foreach (var m in new[] { steady, burst, recovery, oversize })
        {
            var answered = m.Total - m.Count("202 with rejected events") - m.Count("429 without Retry-After") - m.Count("413 without Connection: close");
            if (answered != m.Started)
                problems.Add(LoadSettings.Invariant($"{m.Name}: {m.Started} pushes started, {answered} answered: not one outcome each."));
        }

        foreach (var m in new[] { steady, burst, recovery })
        {
            if (m.Count("202") == 0)
                problems.Add($"{m.Name}: no push was accepted.");
            if (m.Count("429") > 0)
                problems.Add(LoadSettings.Invariant($"{m.Name}: {m.Count("429")} pushes of measured devices were refused with 429."));
            if (m.Count("202 with rejected events") > 0)
                problems.Add(LoadSettings.Invariant($"{m.Name}: {m.Count("202 with rejected events")} pushes had events Ingest refused (first: {firstRejection})."));
            if (m.Count("202") != m.Total - m.Count("202 with rejected events"))
                problems.Add($"{m.Name}: pushes answered other than 202 ({string.Join(", ", JsonSerializer.SerializeToElement(m.Summary(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }).GetProperty("outcomes").EnumerateObject().Select(o => $"{o.Name}: {o.Value}"))}).");
        }

        if (oversize.Total == 0 || oversize.Count("413") != oversize.Started)
            problems.Add(LoadSettings.Invariant($"oversize: {oversize.Started - oversize.Count("413")} of {oversize.Started} bodies over 256 KB were not refused with 413."));
        // ARV-233: the body is refused unread, so the answer closes its connection; one that did not would reset a later push.
        if (oversize.Count("413 without Connection: close") > 0)
            problems.Add(LoadSettings.Invariant($"oversize: {oversize.Count("413 without Connection: close")} refusals with 413 did not close their connection (ARV-233)."));
        if (live.Boards.Total == 0 || live.Boards.Count("200") != live.Boards.Total)
            problems.Add(LoadSettings.Invariant($"displays: {live.Boards.Total - live.Boards.Count("200")} of {live.Boards.Total} board requests did not answer 200."));
        if (live.Screens < settings.Screens)
            problems.Add(LoadSettings.Invariant($"fan-out: {settings.Screens - live.Screens} of {settings.Screens} screens could not connect and join every zone."));
        if (live.Dropped > 0)
            problems.Add(LoadSettings.Invariant($"fan-out: {live.Dropped} screens were disconnected."));
        foreach (var (zone, count) in live.ByZone.Where(z => z.Value == 0))
            problems.Add($"fan-out: Stream announced no snapshot of {zone} to the screens.");
        if (lagSamples.Count == 0)
            problems.Add("Stream's lag could not be read from the broker.");
        // Capacity: Stream ran the whole run (seen as long as Main; on Linux, where the hosts are sampled) and caught up.
        if (OperatingSystem.IsLinux() && streamSeconds < 0.9 * mainSeconds)
            capacity.Add(LoadSettings.Invariant($"Ariva.Api.Stream stopped during the run (seen for {streamSeconds} s of {mainSeconds} s); its log says why."));
        if (engine.CatchUpSeconds is null)
            capacity.Add(LoadSettings.Invariant($"Stream's queue engine was not back to its steady lag ({engine.Steady} records) within {recoverySeconds} s of the burst."));
        if (archive.CatchUpSeconds is null)
            capacity.Add(LoadSettings.Invariant($"The sensing archive was not back to its steady lag ({archive.Steady} records) within {recoverySeconds} s of the burst."));
        if (aman.Count("produced") == 0)
            problems.Add("AMAN feed: nothing was produced.");

        var positions = settings.PeoplePerDevice * settings.SamplesPerPerson;
        var report = new
        {
            Run = "ARV-124a pilot hall lab measurement",
            StartedUtc = started,
            Machine = new { Cores = Environment.ProcessorCount, Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription },
            Shape = new
            {
                site.Site,
                Zones = zones.Count,
                PilotSensors = pilot.Count,
                BurstSensors = every.Count,
                PositionsPerPush = positions,
                SteadyPositionsPerSecond = pilot.Count * positions,
                BurstPositionsPerSecond = every.Count * positions,
                settings.SteadySeconds,
                settings.BurstSeconds,
                RecoverySecondsRun = recoverySeconds,
                settings.Screens,
                Displays = settings.DisplayKeys.Count,
                Desks = site.Desks.Count,
                Gates = site.Gates.Count,
                Partitions = partitions
            },
            Ingest = new[] { steady.Summary(), burst.Summary(), recovery.Summary(), oversize.Summary(), flood.Summary() },
            Live = new
            {
                Connects = live.Connects.Summary(),
                Deliveries = live.Deliveries.Summary(),
                Boards = live.Boards.Summary(),
                live.Screens,
                live.Delivered,
                live.Dropped,
                SnapshotsByZone = live.ByZone,
                live.FewestPerScreen
            },
            // The crossings Ingest accepted, by zone, to compare with what Stream counted (queue_minute entries and exits).
            CrossingsSent = zones.Keys.ToDictionary(z => site.Site + "/" + z, z => new { In = sentIn.GetValueOrDefault(z), Out = sentOut.GetValueOrDefault(z) }, StringComparer.Ordinal),
            Aman = aman.Summary(),
            Stream = new
            {
                QueueEngine = new { SteadyLag = engine.Steady, PeakLag = engine.Peak, engine.CatchUpSeconds },
                SensingArchive = new { SteadyLag = archive.Steady, PeakLag = archive.Peak, archive.CatchUpSeconds },
                ReadErrors = lagErrors,
                Every10Seconds = lagSamples.Where((_, i) => i % 5 == 0).ToList()
            },
            Hosts = hosts,
            Problems = problems,
            Capacity = capacity
        };

        Directory.CreateDirectory(settings.Output);
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        await File.WriteAllTextAsync(Path.Combine(settings.Output, "pilot-report.json"), JsonSerializer.Serialize(report, options), CancellationToken.None);

        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"# Pilot hall run {started:yyyy-MM-dd HH:mm}Z (lab measurement, {report.Machine.Cores} cores)");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"{zones.Count} zones ({settings.HallZones.Count} in the hall), {pilot.Count} sensors steady ({pilot.Count * positions:N0} positions a second), {every.Count} in the burst ({every.Count * positions:N0} a second), {settings.Screens} screens, {settings.DisplayKeys.Count} displays, {site.Desks.Count} desks and {site.Gates.Count} e-gates on AMAN's feed; partitions {string.Join(", ", partitions.Select(p => $"{p.Key} {p.Value}"))}.");
        md.AppendLine();
        md.AppendLine("| Scenario | Requests | Per second | p50 ms | p95 ms | p99 ms | Outcomes |");
        md.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var summary in report.Ingest.Concat([report.Live.Connects, report.Live.Deliveries, report.Live.Boards, report.Aman]))
        {
            var s = JsonSerializer.SerializeToElement(summary, options);
            md.AppendLine(CultureInfo.InvariantCulture, $"| {s.GetProperty("name")} | {s.GetProperty("requests")} | {s.GetProperty("perSecond")} | {s.GetProperty("p50Ms")} | {s.GetProperty("p95Ms")} | {s.GetProperty("p99Ms")} | {string.Join(", ", s.GetProperty("outcomes").EnumerateObject().Select(o => $"{o.Name}: {o.Value}"))} |");
        }

        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"Stream: queue engine steady lag {engine.Steady}, peak {engine.Peak}, back {(engine.CatchUpSeconds is { } e ? e.ToString(CultureInfo.InvariantCulture) + " s" : "not within the run")} after the burst; sensing archive steady {archive.Steady}, peak {archive.Peak}, back {(archive.CatchUpSeconds is { } a ? a.ToString(CultureInfo.InvariantCulture) + " s" : "not within the run")}.");
        md.AppendLine(CultureInfo.InvariantCulture, $"Snapshots delivered by zone: {string.Join(", ", live.ByZone.Select(z => $"{z.Key} {z.Value}"))}; the fewest one screen got: {live.FewestPerScreen}.");
        md.AppendLine();
        md.AppendLine(problems.Count == 0 ? "No problems." : "Problems:\n" + string.Join("\n", problems.Select(p => "- " + p)));
        md.AppendLine(capacity.Count == 0 ? "\nStream kept up." : "\nCapacity:\n" + string.Join("\n", capacity.Select(f => "- " + f)));
        await File.WriteAllTextAsync(Path.Combine(settings.Output, "pilot-report.md"), md.ToString(), CancellationToken.None);
        Console.WriteLine(md.ToString());
        return problems.Count > 0 ? 1 : capacity.Count > 0 ? 2 : 0;
    }

    private static IReadOnlyDictionary<string, int> Outcomes(Measure measure) =>
        JsonSerializer.SerializeToElement(measure.Summary(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            .GetProperty("outcomes").EnumerateObject().ToDictionary(o => o.Name, o => o.Value.GetInt32(), StringComparer.Ordinal);
}

/// <summary>
/// The flooding device's verdict. Ingest accepts exactly the device's limit and refuses the rest with 429 and Retry-After: every
/// push started has exactly one outcome, a 202 or a 429 with Retry-After, and exactly the limit is accepted. Anything else, a
/// 5xx, a transport error (a connection reset included), a 429 without Retry-After or an accepted push with events Ingest refused,
/// is a problem. ARV-124a's runs saw a few resets here, and one on a measured device: the connection Ingest closed after its 413
/// to an oversized push went back to the harness's pool and the next push on it was reset (ARV-233). Since that 413 says
/// Connection: close, a reset is a problem again.
/// </summary>
public static class FloodRule
{
    /// <summary>The problem with the flood's outcomes, or null when it held.</summary>
    public static string Judge(int started, int limit, IReadOnlyDictionary<string, int> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        int Count(string key) => outcomes.TryGetValue(key, out var n) ? n : 0;
        var accepted = Count("202");
        var refused = Count("429");
        var withoutRetryAfter = Count("429 without Retry-After");
        var withRejected = Count("202 with rejected events");
        // The two marks stand beside a 202 or a 429 already counted, so they are not outcomes of their own.
        var others = outcomes.Where(o => o.Key is not ("202" or "429" or "429 without Retry-After" or "202 with rejected events")).Sum(o => o.Value);
        if (started == 0 || accepted + refused + others != started)
            return LoadSettings.Invariant($"flood: {started} pushes started, {accepted + refused + others} answered: not one outcome each.");
        if (accepted != limit)
            return LoadSettings.Invariant($"flood: {accepted} pushes accepted where the device's limit is {limit}.");
        if (refused == 0)
            return "flood: a device far past its rate was never refused with 429.";
        if (withoutRetryAfter > 0)
            return "flood: a 429 came without Retry-After.";
        if (withRejected > 0)
            return LoadSettings.Invariant($"flood: {withRejected} accepted pushes had events Ingest refused.");
        if (others > 0)
            return LoadSettings.Invariant($"flood: {others} pushes failed (5xx or a transport error, a connection reset included: ARV-233).");
        return null;
    }

    /// <summary>The rule's table: throws when a case is judged otherwise.</summary>
    public static void SelfTest()
    {
        const string Reset = "error HttpRequestException ";
        var cases = new (int Started, Dictionary<string, int> Outcomes, bool Problem)[]
        {
            (900, new() { ["202"] = 600, ["429"] = 300 }, false),
            (900, new() { ["202"] = 600, ["429"] = 299, [Reset + "ResponseEnded"] = 1 }, true),
            (900, new() { ["202"] = 600, ["429"] = 299, [Reset + "Unknown"] = 1 }, true),
            (900, new() { ["202"] = 600, ["429"] = 299, [Reset + "ConnectionError"] = 1 }, true),
            (900, new() { ["202"] = 601, ["429"] = 299 }, true),
            (900, new() { ["202"] = 600, ["429"] = 299, ["503"] = 1 }, true),
            (900, new() { ["202"] = 600, ["429"] = 299 }, true),
            (900, new() { ["202"] = 600, ["429"] = 300, ["429 without Retry-After"] = 1 }, true),
            (900, new() { ["202"] = 600, ["429"] = 0, [Reset + "Unknown"] = 300 }, true),
            (900, new() { ["202"] = 600, ["429"] = 300, ["202 with rejected events"] = 1 }, true),
            (900, new() { ["202"] = 600, ["429"] = 300, [Reset + "Unknown"] = 1 }, true),
            (1200, new() { ["202"] = 600, ["429"] = 600 }, false),
            (0, new(), true)
        };
        foreach (var (started, outcomes, problem) in cases)
        {
            if ((Judge(started, 600, outcomes) is not null) != problem)
                throw new InvalidOperationException(LoadSettings.Invariant($"FloodRule judged {string.Join(", ", outcomes.Select(o => $"{o.Key}: {o.Value}"))} of {started} otherwise than its table."));
        }
    }
}
