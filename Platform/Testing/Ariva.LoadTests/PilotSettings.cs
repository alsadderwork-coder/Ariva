using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ariva.LoadTests;

/// <summary>A queue zone of the site: its name and where its 24 x 12 m rectangle starts on the level (x0 to x0 + 24, y 10 to 22).</summary>
public sealed record PilotZone(string Name, double X0);

/// <summary>A sensor of the pilot hall: the queue zone it is commissioned for, its credential, and whether it pushes in the burst only.</summary>
public sealed record PilotDevice(string Zone, string Key, bool Burst);

/// <summary>A border desk on AMAN's feed: its AMAN code (mapped to an Ariva desk by the provisioning side) and its lane.</summary>
public sealed record PilotDesk(string Code, string Lane);

/// <summary>The site the provisioning side built (ARIVA_LOAD_PILOT, JSON): its zones, sensors, desks and e-gates on AMAN's feed.</summary>
public sealed record PilotSite(string Site, IReadOnlyList<PilotZone> Zones, IReadOnlyList<PilotDevice> Devices, PilotDevice Flood, IReadOnlyList<PilotDesk> Desks,
    IReadOnlyList<string> Gates);

/// <summary>
/// ARV-124a: the pilot hall profile (Proposed: up to 20 desks, e-gates, 30 T3 sensors, AMAN feed, 50 dashboards, 10
/// displays) and the burst at the D5 site rate (100 T3 sensors at 30 people and 5 Hz: 15,000 positions a second), through
/// the whole pipeline: Ariva.Api.Ingest, Kafka, Ariva.Api.Stream (its own snapshots and desk feed), Ariva.Api.Integration
/// (AMAN's feed from Kafka), the live hub and the boards. From environment variables only, as <see cref="LoadSettings"/>:
/// the credentials never appear on a command line.
/// </summary>
public sealed record PilotSettings
{
    public required Uri Ingest { get; init; }
    public required Uri Main { get; init; }
    public required PilotSite Site { get; init; }
    public required IReadOnlyList<string> DisplayKeys { get; init; }
    public required IReadOnlyList<string> DisplayCodes { get; init; }
    public required IReadOnlyList<string> AccessTokens { get; init; }
    /// <summary>The run's Kafka (ARIVA_LOAD_KAFKA_BOOTSTRAP): AMAN's feed is produced to it and Stream's lag read from it.</summary>
    public required string KafkaBootstrap { get; init; }
    public required string Output { get; init; }

    /// <summary>The pilot hall at its rate: its 30 sensors, a push a second each, AMAN's minutes, the screens and boards.</summary>
    public int SteadySeconds { get; init; } = 300;
    /// <summary>Every sensor of the site (the pilot's and the burst's, 100 in all) at a push a second: the D5 site rate.</summary>
    public int BurstSeconds { get; init; } = 60;
    /// <summary>The pilot's sensors again while Stream catches up; the run ends early once its lag is back to the steady value.</summary>
    public int RecoverySeconds { get; init; } = 300;
    public int Screens => 50;
    public int DisplayPollSeconds => 10;
    /// <summary>People a sensor sees at once, each sampled at 5 Hz and sent once a second: 150 positions a push (a T3 sensor).</summary>
    public int PeoplePerDevice => 30;
    public int SamplesPerPerson => 5;
    /// <summary>How long a person takes from a zone's entry line to its exit line; then the next person takes the place.</summary>
    public int DwellSeconds => 300;
    /// <summary>The flood: one device far past its limit (600 a minute) during the burst.</summary>
    public int FloodPerSecond => 150;
    public int FloodSeconds => 6;

    private static readonly JsonSerializerOptions Strict = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public static PilotSettings FromEnvironment()
    {
        static string Need(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"{name} is required.");
        static IReadOnlyList<string> List(string name) => [.. Need(name).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        static int Seconds(string name, int fallback, int max) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
                ? int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n is >= 10 && n <= max
                    ? n
                    : throw new InvalidOperationException(LoadSettings.Invariant($"{name} is 10 to {max} seconds."))
                : fallback;

        // Never echo the variable: it holds the sensors' credentials.
        PilotSite site;
        try
        {
            site = JsonSerializer.Deserialize<PilotSite>(Need("ARIVA_LOAD_PILOT"), Strict);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("ARIVA_LOAD_PILOT is not the pilot site's JSON.");
        }

        if (site?.Zones is not { Count: > 0 and <= 40 } || site.Devices is not { Count: > 0 and <= 200 } || site.Flood is null || site.Desks is not { Count: <= 100 } ||
            site.Gates is not { Count: <= 100 } || string.IsNullOrEmpty(site.Site))
            throw new InvalidOperationException("ARIVA_LOAD_PILOT needs a site, 1 to 40 zones, 1 to 200 devices, a flood device and at most 100 desks and gates.");
        var zones = site.Zones.Select(z => z.Name).ToHashSet(StringComparer.Ordinal);
        if (site.Devices.Append(site.Flood).Any(d => !zones.Contains(d.Zone) || string.IsNullOrEmpty(d.Key)))
            throw new InvalidOperationException("ARIVA_LOAD_PILOT names a device without a credential or outside its zones.");

        var settings = new PilotSettings
        {
            Ingest = new Uri(Need("ARIVA_LOAD_INGEST_URL")),
            Main = new Uri(Need("ARIVA_LOAD_MAIN_URL")),
            Site = site,
            DisplayKeys = List("ARIVA_LOAD_DISPLAY_KEYS"),
            DisplayCodes = List("ARIVA_LOAD_DISPLAY_CODES"),
            AccessTokens = List("ARIVA_LOAD_ACCESS_TOKENS"),
            KafkaBootstrap = Need("ARIVA_LOAD_KAFKA_BOOTSTRAP"),
            Output = Environment.GetEnvironmentVariable("ARIVA_LOAD_OUTPUT") is { Length: > 0 } output ? output : Path.Combine(".verify", "load", "pilot"),
            SteadySeconds = Seconds("ARIVA_LOAD_STEADY_SECONDS", 300, 3600),
            BurstSeconds = Seconds("ARIVA_LOAD_BURST_SECONDS", 60, 600),
            RecoverySeconds = Seconds("ARIVA_LOAD_RECOVERY_SECONDS", 300, 3600)
        };
        if (settings.DisplayCodes.Count != settings.DisplayKeys.Count)
            throw new InvalidOperationException("ARIVA_LOAD_DISPLAY_CODES needs one code for each of ARIVA_LOAD_DISPLAY_KEYS.");
        // ARV-128 (CWE-918): the pilot run writes AMAN's feed and loads the hosts, so it runs only against this machine, as the
        // E2E suites do; the message names the variable, never its value.
        if (!Loopback(settings.Ingest.Host))
            throw new InvalidOperationException("ARIVA_LOAD_INGEST_URL is not on this machine: the pilot run loads only local hosts.");
        if (!Loopback(settings.Main.Host))
            throw new InvalidOperationException("ARIVA_LOAD_MAIN_URL is not on this machine: the pilot run loads only local hosts.");
        if (!settings.KafkaBootstrap.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(b => Loopback(BootstrapHost(b))))
            throw new InvalidOperationException("ARIVA_LOAD_KAFKA_BOOTSTRAP is not on this machine: the pilot run writes AMAN's feed to a local Kafka only.");
        if (settings.AccessTokens.Count * LoadSettings.ScreensPerSession < settings.Screens)
            throw new InvalidOperationException(LoadSettings.Invariant($"{settings.Screens} screens need at least {(settings.Screens + LoadSettings.ScreensPerSession - 1) / LoadSettings.ScreensPerSession} sessions in ARIVA_LOAD_ACCESS_TOKENS."));
        return settings;
    }

    private static bool Loopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var address) && System.Net.IPAddress.IsLoopback(address));

    /// <summary>The host of a Kafka bootstrap entry (host:port, [v6]:port or host).</summary>
    private static string BootstrapHost(string entry) =>
        entry.StartsWith('[') ? entry[..(entry.IndexOf(']', StringComparison.Ordinal) + 1)] : entry.Split(':')[0];

    /// <summary>The hall's zones (those of the pilot's own sensors); the burst's sensors are on zones of their own, as a D5 site spreads them.</summary>
    public IReadOnlyList<string> HallZones => [.. Site.Zones.Select(z => z.Name).Where(z => Site.Devices.Any(d => !d.Burst && d.Zone == z))];

    /// <summary>The screens watch the hall's zones.</summary>
    public FanoutPlan Fanout() => new(Main, [.. HallZones.Select(z => Site.Site + "/" + z)], Screens, AccessTokens, DisplayKeys, DisplayCodes, DisplayPollSeconds, 0, null, null);
}
