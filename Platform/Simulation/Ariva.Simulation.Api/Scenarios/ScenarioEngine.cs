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

/// <summary>One sensor at a clock minute of the simulated day: its zone, the queue zone of its device and its role.</summary>
public sealed record ScenarioSensorState(string Sensor, string Zone, string Type, string Level, bool Offline)
{
    public string QueueZone { get; init; }

    public Emulators.Sensors.SensorRole Role { get; init; }
}

/// <summary>A request to re-run a site's day with another seed (required); the site is DMO unless named (ARV-139b).</summary>
public sealed record RerunScenarioRequest(long? Seed)
{
    [System.ComponentModel.DataAnnotations.MaxLength(17)]
    public string Site { get; init; }
}

/// <summary>
/// The simulator's simulated days (ARV-027; ARV-139b: one per scenario site). At start each site runs with its
/// configured seed (<c>Simulation:Sites:{site}:Seed</c>; DMO also reads the older <c>Simulation:Seed</c>): the reference
/// scenario at DMO (9303 by default) and the illustrative AUH Terminal A arrivals evening at AUH-TA (9304 by default).
/// A site's day is re-run on request. Readers and re-runs are serialised because a day caches bins as they finalise; a
/// run takes a fraction of a second, and re-runs go one at a time so that a burst of them cannot pile up on the CPU. The
/// days live in this replica only: the chart runs one replica (simulationHpaMax 1).
/// </summary>
public sealed class ScenarioEngine : IDisposable
{
    /// <summary>Clock minutes of the day.</summary>
    public const int Minutes = ScenarioModel.Day;

    /// <summary>The reference site, the default of every endpoint and emulator that names none.</summary>
    public const string ReferenceSite = ScenarioModel.SiteCode;

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _runs = new(1, 1);
    private readonly ILogger<ScenarioEngine> _logger;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, (ScenarioDay Day, DateTimeOffset RunAt, string RunBy)> _days = new(StringComparer.Ordinal);

    public ScenarioEngine(IConfiguration configuration, ILogger<ScenarioEngine> logger, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _logger = logger;
        _time = time;
        foreach (var site in ScenarioSites.All)
        {
            var key = $"Simulation:Sites:{site.Code}:Seed";
            var seed = configuration.GetValue<long?>(key)
                       ?? (site == ScenarioSites.Dmo ? configuration.GetValue<long?>("Simulation:Seed") : null)
                       ?? site.DefaultSeed;
            if (seed is < 0 or > uint.MaxValue)
                throw new InvalidOperationException($"The seed of scenario site {site.Code} (Simulation:Seed or {key}) must be between 0 and 4294967295.");
            _days[site.Code] = (ScenarioDay.Run(ScenarioConfig.ForSite(site.Code, (uint)seed)), time.GetUtcNow(), "configuration");
        }
    }

    /// <summary>The scenario sites the engine plays (DMO, AUH-TA).</summary>
    public static IReadOnlyList<string> SiteCodes { get; } = [.. ScenarioSites.All.Select(s => s.Code)];

    /// <summary>Whether the engine plays a site (ordinal, exact code).</summary>
    public static bool HasSite(string site) => ScenarioSites.Find(site) is not null;

    /// <summary>The IATA code of a scenario site's airport (DMO: DMO; AUH-TA: AUH), or null.</summary>
    public static string AirportOf(string site) => ScenarioSites.Find(site)?.Airport;

    /// <summary>The reference site's current day, for the emulators: they read only what a run leaves fixed (flows and cumulative curves).</summary>
    internal ScenarioDay CurrentDay => DayOf(ReferenceSite);

    /// <summary>A site's current day (throws for a site the engine does not play).</summary>
    internal ScenarioDay DayOf(string site)
    {
        lock (_gate)
            return Entry(site).Day;
    }

    /// <summary>Reads the reference site's current day under the engine's lock (views of a day are not thread safe), for the feed emulators.</summary>
    internal T Read<T>(Func<ScenarioDay, T> read) => Read(ReferenceSite, read);

    /// <summary>Reads a site's current day under the engine's lock.</summary>
    internal T Read<T>(string site, Func<ScenarioDay, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_gate)
            return read(Entry(site).Day);
    }

    /// <summary>A site's summary (the reference site's when none is named).</summary>
    public ScenarioSummary Summary(string site = ReferenceSite)
    {
        lock (_gate)
            return SummaryOf(Entry(site));
    }

    /// <summary>
    /// Re-runs a site's scenario with another seed, one re-run at a time; <paramref name="by"/> is the operator key's name,
    /// for the audit log. Readers keep the previous day until the new one is ready.
    /// </summary>
    public Task<ScenarioSummary> RerunAsync(uint seed, string by, CancellationToken ct) => RerunAsync(ReferenceSite, seed, by, ct);

    /// <inheritdoc cref="RerunAsync(uint, string, CancellationToken)"/>
    public async Task<ScenarioSummary> RerunAsync(string site, uint seed, string by, CancellationToken ct)
    {
        var config = ScenarioConfig.ForSite(site, seed) ?? throw new ArgumentException("The site is one the simulator plays.", nameof(site));
        await _runs.WaitAsync(ct);
        try
        {
            var day = ScenarioDay.Run(config);
            lock (_gate)
            {
                var previous = Entry(site).Day.Seed;
                _days[day.Site.Code] = (day, _time.GetUtcNow(), by);
                _logger.LogInformation("Scenario {Site} re-run by {Operator}: seed {PreviousSeed} to {Seed}, {Alerts} alerts", day.Site.Code, by, previous, seed,
                    day.Alerts.Count);
                return SummaryOf(_days[day.Site.Code]);
            }
        }
        finally
        {
            _runs.Release();
        }
    }

    public void Dispose() => _runs.Dispose();

    /// <summary>Every queue of a site at a clock minute (0 to 1439).</summary>
    public IReadOnlyList<ScenarioQueueState> States(int minute, string site = ReferenceSite)
    {
        lock (_gate)
        {
            var day = Entry(site).Day;
            return day.States(minute).Select(s => View(day, s)).ToList();
        }
    }

    /// <summary>One queue of a site at a clock minute, or null when the site has no such queue.</summary>
    public ScenarioQueueState State(string queue, int minute, string site = ReferenceSite)
    {
        lock (_gate)
        {
            var day = Entry(site).Day;
            if (queue is null || !day.Site.QueueIndex.TryGetValue(queue, out var q))
                return null;
            return View(day, day.State(q, minute));
        }
    }

    /// <summary>Every alert of a site's day under its seeded rules, by raise time.</summary>
    public IReadOnlyList<ScenarioAlert> Alerts(string site = ReferenceSite)
    {
        lock (_gate)
            return Entry(site).Day.Alerts;
    }

    /// <summary>Every sensor of a site at a clock minute.</summary>
    public IReadOnlyList<ScenarioSensorState> Sensors(int minute, string site = ReferenceSite)
    {
        var scenario = ScenarioSites.Find(site) ?? throw new ArgumentException("The site is one the simulator plays.", nameof(site));
        return scenario.Sensors.Select(s => new ScenarioSensorState(s.Id, s.Zone, s.Type, s.Level, ScenarioDay.SensorOfflineAt(scenario, s.Id, minute))
        {
            QueueZone = s.QueueZone, Role = s.Role
        }).ToList();
    }

    private (ScenarioDay Day, DateTimeOffset RunAt, string RunBy) Entry(string site) =>
        site is not null && _days.TryGetValue(site, out var entry) ? entry : throw new ArgumentException("The site is one the simulator plays.", nameof(site));

    private static ScenarioSummary SummaryOf((ScenarioDay Day, DateTimeOffset RunAt, string RunBy) entry)
    {
        var day = entry.Day;
        return new ScenarioSummary(day.Seed, day.Site.Date, day.Site.Code, day.Site.NQ, day.Site.Sensors.Count, day.Schedule.Arrivals.Count,
            day.Schedule.Departures.Count, day.Alerts.Count, entry.RunAt, entry.RunBy);
    }

    private static ScenarioQueueState View(ScenarioDay day, QueueState s)
    {
        var def = day.Site.Queues[s.Q];
        return new ScenarioQueueState(def.Id, def.Name, def.Group, s.Length, s.Rate, s.Open, s.Paused, s.Nowcast, s.Degraded, s.Band, s.Arrivals, s.Served);
    }
}
