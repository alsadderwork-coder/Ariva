using Ariva.Simulation.Api.Scenarios.Engine;

namespace Ariva.Simulation.Api.Scenarios;

/// <summary>What the simulated day is: seed, date, site and how much it holds.</summary>
public sealed record ScenarioSummary(uint Seed, string Date, string SiteCode, int Queues, int Sensors, int Arrivals, int Departures, int Alerts, DateTimeOffset RunAt, string RunBy);

/// <summary>One queue at a clock minute of the simulated day.</summary>
public sealed record ScenarioQueueState(
    string Queue,
    string Name,
    string Group,
    double Length,
    double Throughput,
    int Open,
    int Paused,
    double? NowcastMinutes,
    bool Degraded,
    IReadOnlyList<int> NowcastBand,
    double Arrivals,
    double Served);

/// <summary>One sensor at a clock minute of the simulated day.</summary>
public sealed record ScenarioSensorState(string Sensor, string Zone, string Type, string Level, bool Offline);

/// <summary>A request to re-run the day with another seed (required).</summary>
public sealed record RerunScenarioRequest(long? Seed);

/// <summary>
/// The simulator's one simulated day (ARV-027): the reference scenario with the configured seed (9303 by default) at
/// start, re-run on request. Readers and re-runs are serialised because a day caches bins as they finalise; a run takes
/// a fraction of a second, and re-runs go one at a time so that a burst of them cannot pile up on the CPU. The day
/// lives in this replica only: the chart runs one replica (simulationHpaMax 1).
/// </summary>
public sealed class ScenarioEngine : IDisposable
{
    /// <summary>Clock minutes of the day.</summary>
    public const int Minutes = ScenarioModel.Day;

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _runs = new(1, 1);
    private readonly ILogger<ScenarioEngine> _logger;
    private readonly TimeProvider _time;
    private readonly string _siteCode;
    private ScenarioDay _day;
    private DateTimeOffset _runAt;
    private string _runBy;

    public ScenarioEngine(IConfiguration configuration, ILogger<ScenarioEngine> logger, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _logger = logger;
        _time = time;
        _siteCode = configuration["Simulation:SiteCode"] ?? "DMO";
        var seed = configuration.GetValue<long?>("Simulation:Seed") ?? ScenarioModel.DefaultSeed;
        if (seed is < 0 or > uint.MaxValue)
            throw new InvalidOperationException("Simulation:Seed must be between 0 and 4294967295.");
        _day = ScenarioDay.Run(ScenarioConfig.Reference((uint)seed));
        _runAt = time.GetUtcNow();
        _runBy = "configuration";
    }

    /// <summary>The current day, for the emulators: they read only what a run leaves fixed (flows and cumulative curves).</summary>
    internal ScenarioDay CurrentDay
    {
        get
        {
            lock (_gate)
                return _day;
        }
    }

    /// <summary>Reads the current day under the engine's lock (views of a day are not thread safe), for the feed emulators.</summary>
    internal T Read<T>(Func<ScenarioDay, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_gate)
            return read(_day);
    }

    /// <summary>The day's summary.</summary>
    public ScenarioSummary Summary()
    {
        lock (_gate)
            return SummaryOf(_day);
    }

    /// <summary>
    /// Re-runs the reference scenario with another seed, one re-run at a time; <paramref name="by"/> is the operator
    /// key's name, for the audit log. Readers keep the previous day until the new one is ready.
    /// </summary>
    public async Task<ScenarioSummary> RerunAsync(uint seed, string by, CancellationToken ct)
    {
        await _runs.WaitAsync(ct);
        try
        {
            var day = ScenarioDay.Run(ScenarioConfig.Reference(seed));
            lock (_gate)
            {
                var previous = _day.Seed;
                _day = day;
                _runAt = _time.GetUtcNow();
                _runBy = by;
                _logger.LogInformation("Scenario re-run by {Operator}: seed {PreviousSeed} to {Seed}, {Alerts} alerts", by, previous, seed, day.Alerts.Count);
                return SummaryOf(day);
            }
        }
        finally
        {
            _runs.Release();
        }
    }

    public void Dispose() => _runs.Dispose();

    /// <summary>Every queue at a clock minute (0 to 1439).</summary>
    public IReadOnlyList<ScenarioQueueState> States(int minute)
    {
        lock (_gate)
            return _day.States(minute).Select(View).ToList();
    }

    /// <summary>One queue at a clock minute, or null when the queue does not exist.</summary>
    public ScenarioQueueState State(string queue, int minute)
    {
        if (queue is null || !ScenarioModel.QueueIndex.TryGetValue(queue, out var q))
            return null;
        lock (_gate)
            return View(_day.State(q, minute));
    }

    /// <summary>Every alert of the day under the seeded rules, by raise time.</summary>
    public IReadOnlyList<ScenarioAlert> Alerts()
    {
        lock (_gate)
            return _day.Alerts;
    }

    /// <summary>Every sensor at a clock minute.</summary>
    public IReadOnlyList<ScenarioSensorState> Sensors(int minute) =>
        ScenarioModel.Sensors.Select(s => new ScenarioSensorState(s.Id, s.Zone, s.Type, s.Level, ScenarioDay.SensorOffline(s.Id, minute))).ToList();

    private ScenarioSummary SummaryOf(ScenarioDay day) => new(day.Seed, ScenarioModel.Date, _siteCode, ScenarioModel.NQ, ScenarioModel.Sensors.Count,
        day.Schedule.Arrivals.Count, day.Schedule.Departures.Count, day.Alerts.Count, _runAt, _runBy);

    private static ScenarioQueueState View(QueueState s)
    {
        var def = ScenarioModel.Queues[s.Q];
        return new ScenarioQueueState(def.Id, def.Name, def.Group, s.Length, s.Rate, s.Open, s.Paused, s.Nowcast, s.Degraded, s.Band, s.Arrivals, s.Served);
    }
}
