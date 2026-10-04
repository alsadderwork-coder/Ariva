using System.Globalization;

namespace Ariva.LoadTests;

/// <summary>
/// What a run targets and how hard, from environment variables only: credentials never appear on a command line (where
/// any local user could read them in the process list). The provisioning side (the E2E spec, or an operator in a lab)
/// registers the devices, displays and the screen account and passes their credentials here.
/// </summary>
public sealed record LoadSettings
{
    public required bool Full { get; init; }
    public required Uri Ingest { get; init; }
    public required Uri Main { get; init; }
    /// <summary>The queue zone the devices are commissioned for (the push path's zone name).</summary>
    public required string ZoneName { get; init; }
    /// <summary>The live zone key, "SITE/Zone name".</summary>
    public required string ZoneKey { get; init; }
    public required IReadOnlyList<string> DeviceKeys { get; init; }
    /// <summary>A device kept apart for the flood scenario, so its 429s never touch the measured devices' budget.</summary>
    public required string FloodKey { get; init; }
    public required IReadOnlyList<string> DisplayKeys { get; init; }
    /// <summary>The displays' codes, in the order of <see cref="DisplayKeys"/> (a player presents both).</summary>
    public required IReadOnlyList<string> DisplayCodes { get; init; }
    /// <summary>
    /// Access tokens with LiveQueue.View on the zone's site, one per signed-in session; the screens take them in turn. The
    /// hub holds at most <see cref="ScreensPerSession"/> connections per session (HubSessionRegistry, CWE-400), so a run
    /// needs at least <see cref="Screens"/> / <see cref="ScreensPerSession"/> of them, as a wall of screens needs a sign-in each.
    /// </summary>
    public required IReadOnlyList<string> AccessTokens { get; init; }
    /// <summary>Redis of the run, for publishing snapshots as Ariva.Api.Stream does; without it the fan-out only connects.</summary>
    public string RedisUrl { get; init; }
    public string RedisInstance { get; init; } = "ariva:";
    public required string Output { get; init; }

    // The shape of the run: smoke (the E2E gate, under the default limits) or full (the lab measurement).
    public int SteadySeconds => Full ? 120 : 15;
    public double SteadyPushesPerSecondPerDevice => Full ? 1.0 : 0.2;
    public int BurstSeconds => Full ? 30 : 10;
    public double BurstFactor => 3;
    public int Screens => Full ? 150 : 40;
    public int DisplayPollSeconds => 10;
    public int FanoutSeconds => SteadySeconds + BurstSeconds;
    /// <summary>The flood: far past a device's limit (the E2E ingest sets 120 a minute, a lab install keeps 600).</summary>
    public int FloodPerSecond => Full ? 150 : 40;
    public int FloodSeconds => Full ? 6 : 5;
    public int PeoplePerPush => 30;
    /// <summary>HubSessionRegistry.MaxConnectionsPerSession: the most screens one session may hold on one replica.</summary>
    public const int ScreensPerSession = 8;
    public int SamplesPerPerson => 5;

    public static LoadSettings FromEnvironment()
    {
        static string Need(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"{name} is required.");
        static IReadOnlyList<string> List(string name) => [.. Need(name).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        var zone = Need("ARIVA_LOAD_ZONE_KEY");
        var settings = new LoadSettings
        {
            Full = string.Equals(Environment.GetEnvironmentVariable("ARIVA_LOAD_MODE"), "full", StringComparison.OrdinalIgnoreCase),
            Ingest = new Uri(Need("ARIVA_LOAD_INGEST_URL")),
            Main = new Uri(Need("ARIVA_LOAD_MAIN_URL")),
            ZoneKey = zone,
            ZoneName = zone[(zone.IndexOf('/', StringComparison.Ordinal) + 1)..],
            DeviceKeys = List("ARIVA_LOAD_DEVICE_KEYS"),
            FloodKey = Need("ARIVA_LOAD_FLOOD_KEY"),
            DisplayKeys = List("ARIVA_LOAD_DISPLAY_KEYS"),
            DisplayCodes = List("ARIVA_LOAD_DISPLAY_CODES"),
            AccessTokens = List("ARIVA_LOAD_ACCESS_TOKENS"),
            RedisUrl = Environment.GetEnvironmentVariable("ARIVA_LOAD_REDIS_URL"),
            RedisInstance = Environment.GetEnvironmentVariable("ARIVA_LOAD_REDIS_INSTANCE") is { Length: > 0 } instance ? instance : "ariva:",
            Output = Environment.GetEnvironmentVariable("ARIVA_LOAD_OUTPUT") is { Length: > 0 } output ? output : Path.Combine(".verify", "load")
        };
        if (settings.DisplayCodes.Count != settings.DisplayKeys.Count)
            throw new InvalidOperationException("ARIVA_LOAD_DISPLAY_CODES needs one code for each of ARIVA_LOAD_DISPLAY_KEYS.");
        if (settings.AccessTokens.Count * ScreensPerSession < settings.Screens)
            throw new InvalidOperationException(Invariant($"{settings.Screens} screens need at least {(settings.Screens + ScreensPerSession - 1) / ScreensPerSession} sessions in ARIVA_LOAD_ACCESS_TOKENS (at most {ScreensPerSession} each)."));
        return settings;
    }

    public static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
