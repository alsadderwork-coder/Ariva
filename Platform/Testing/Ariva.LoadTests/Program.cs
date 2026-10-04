using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Ariva.LoadTests;

// ARV-071 load run: the screens connect and stay while the sensors push at their rate and then three times it, with one
// device flooding past its rate during the burst; then bodies over 256 KB. Settings come from ARIVA_LOAD_* environment
// variables (LoadSettings). Writes load-report.json and load-report.md to ARIVA_LOAD_OUTPUT and exits 1 when a request
// failed (5xx or transport error), a limit did not answer 413 or 429 with Retry-After, a measured device was refused
// with 429, a screen could not join, screens missed snapshots, or a display board did not answer 200.

var settings = LoadSettings.FromEnvironment();
using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(settings.Full ? 15 : 5));
var ingest = new IngestScenarios(settings, ArivaApis.Ingest(settings.Ingest));
var fanout = new LiveFanout(settings, ArivaApis.Display(settings.Main));
var started = DateTime.UtcNow;

Console.WriteLine(LoadSettings.Invariant($"ARV-071 load run ({(settings.Full ? "full" : "smoke")}): {settings.DeviceKeys.Count} devices, {settings.Screens} screens, {settings.DisplayKeys.Count} displays."));
Measure steady, burst, oversize, flood;
LiveFanout.Result live;
object hosts;
await using (var sampler = new HostSampler())
{
    var liveTask = fanout.RunAsync(cancel.Token);
    // The screens come up first.
    await Task.Delay(TimeSpan.FromSeconds(3), cancel.Token);
    steady = await ingest.PushesAsync("steady", settings.SteadyPushesPerSecondPerDevice, settings.SteadySeconds, cancel.Token);
    // The flood runs during the burst: one device past its rate must not cost the 40 measured devices their budget.
    var burstTask = ingest.PushesAsync("burst", settings.SteadyPushesPerSecondPerDevice * settings.BurstFactor, settings.BurstSeconds, cancel.Token);
    await Task.Delay(TimeSpan.FromSeconds(2), cancel.Token);
    flood = await ingest.FloodAsync(cancel.Token);
    burst = await burstTask;
    live = await liveTask;
    oversize = await ingest.OversizeAsync(cancel.Token);
    hosts = sampler.Summary();
}

var problems = new List<string>();
foreach (var m in new[] { steady, burst, oversize, flood, live.Connects, live.Boards })
{
    if (m.Failures > 0)
        problems.Add(LoadSettings.Invariant($"{m.Name}: {m.Failures} failed requests (5xx or transport)."));
}

if (steady.Count("202") == 0 || burst.Count("202") == 0)
    problems.Add("steady or burst: no push was accepted.");
if (oversize.Count("413") != oversize.Total)
    problems.Add(LoadSettings.Invariant($"oversize: {oversize.Total - oversize.Count("413")} of {oversize.Total} bodies over 256 KB were not refused with 413."));
if (steady.Count("429") > 0 || burst.Count("429") > 0)
    problems.Add(LoadSettings.Invariant($"steady or burst: {steady.Count("429") + burst.Count("429")} pushes of the measured devices were refused with 429 (the flooding device's limit leaked)."));
if (flood.Count("429") == 0)
    problems.Add("flood: a device far past its rate was never refused with 429.");
if (flood.Count("429 without Retry-After") > 0)
    problems.Add("flood: a 429 came without Retry-After.");
if (live.Boards.Total == 0 || live.Boards.Count("200") != live.Boards.Total)
    problems.Add(LoadSettings.Invariant($"displays: {live.Boards.Total - live.Boards.Count("200")} of {live.Boards.Total} board requests did not answer 200."));
if (live.Screens < settings.Screens)
    problems.Add(LoadSettings.Invariant($"fan-out: {settings.Screens - live.Screens} of {settings.Screens} screens could not connect and join."));
if (live.Published > 0 && live.Delivered < 0.95 * live.Published * live.Screens)
    problems.Add(LoadSettings.Invariant($"fan-out: {live.Delivered} of {live.Published * live.Screens} snapshots delivered (under 95 percent)."));
if (live.Dropped > 0)
    problems.Add(LoadSettings.Invariant($"fan-out: {live.Dropped} screens were disconnected."));

var report = new
{
    Run = "ARV-071 lab measurement",
    Mode = settings.Full ? "full" : "smoke",
    StartedUtc = started,
    Machine = new { Cores = Environment.ProcessorCount, Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription },
    Shape = new
    {
        Devices = settings.DeviceKeys.Count,
        settings.SteadyPushesPerSecondPerDevice,
        settings.SteadySeconds,
        settings.BurstFactor,
        settings.BurstSeconds,
        PositionsPerPush = settings.PeoplePerPush * settings.SamplesPerPerson,
        settings.Screens,
        Displays = settings.DisplayKeys.Count,
        settings.DisplayPollSeconds
    },
    Ingest = new[] { steady.Summary(), burst.Summary(), oversize.Summary(), flood.Summary() },
    Live = new
    {
        Connects = live.Connects.Summary(),
        Deliveries = live.Deliveries.Summary(),
        Boards = live.Boards.Summary(),
        live.Published,
        live.Screens,
        live.Delivered,
        live.Dropped
    },
    Hosts = hosts,
    Problems = problems
};

Directory.CreateDirectory(settings.Output);
var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var json = JsonSerializer.Serialize(report, options);
await File.WriteAllTextAsync(Path.Combine(settings.Output, "load-report.json"), json, cancel.Token);

var md = new StringBuilder();
md.AppendLine(CultureInfo.InvariantCulture, $"# Load run {started:yyyy-MM-dd HH:mm}Z ({report.Mode}, lab measurement, {report.Machine.Cores} cores)");
md.AppendLine();
md.AppendLine("| Scenario | Requests | Per second | p50 ms | p95 ms | p99 ms | Outcomes |");
md.AppendLine("|---|---|---|---|---|---|---|");
foreach (var summary in report.Ingest.Concat([report.Live.Connects, report.Live.Deliveries, report.Live.Boards]))
{
    var s = JsonSerializer.SerializeToElement(summary, options);
    md.AppendLine(CultureInfo.InvariantCulture, $"| {s.GetProperty("name")} | {s.GetProperty("requests")} | {s.GetProperty("perSecond")} | {s.GetProperty("p50Ms")} | {s.GetProperty("p95Ms")} | {s.GetProperty("p99Ms")} | {string.Join(", ", s.GetProperty("outcomes").EnumerateObject().Select(o => $"{o.Name}: {o.Value}"))} |");
}

md.AppendLine();
md.AppendLine(problems.Count == 0 ? "No problems." : "Problems:\n" + string.Join("\n", problems.Select(p => "- " + p)));
await File.WriteAllTextAsync(Path.Combine(settings.Output, "load-report.md"), md.ToString(), cancel.Token);
Console.WriteLine(md.ToString());
return problems.Count == 0 ? 0 : 1;
