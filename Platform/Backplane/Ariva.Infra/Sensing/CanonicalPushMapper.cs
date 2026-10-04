using System.Text.Json;
using System.Text.Json.Serialization;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Sensing;

namespace Ariva.Infra.Sensing;

/// <summary>
/// The canonical dialect (ARV-023): Ariva's own events as JSON, for gateways and devices that speak them. Strict: an
/// unknown member, a wrong type, a number for an enum or a time without "Z" or "+00:00" refuses the whole message.
/// <code>
/// { "sentUtc": "2026-10-02T18:05:00.250Z", "packageId": 4711,
///   "tracks":    [{ "trackId": "7", "x": 12.5, "y": 8.25, "heightMetres": 1.72, "timeUtc": "2026-10-02T18:04:59.800Z" }],
///   "crossings": [{ "lineName": "Entry A", "direction": "In", "trackId": "7", "timeUtc": "..." }],
///   "occupancy": [{ "zoneName": "Snake A", "count": 41, "timeUtc": "..." }],
///   "intervals": [{ "lineName": "Entry A", "in": 30, "out": 2, "fromUtc": "...", "toUtc": "..." }],
///   "status":    { "online": true, "temperatureCelsius": 41.5, "frameRate": 12.5, "clockOffsetMilliseconds": -18, "timeUtc": "..." } }
/// </code>
/// Positions are floor coordinates of the device's level in metres.
/// </summary>
public static class CanonicalPushMapper
{
    public const DeviceDialect Dialect = DeviceDialect.Canonical;

    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        MaxDepth = 8
    };

    public static MappedPush Map(JsonElement root, int maxEvents)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new PushFormatException("A canonical push is a JSON object.");
        Payload payload;
        try
        {
            payload = root.Deserialize<Payload>(Strict) ?? throw new PushFormatException("The push is empty.");
        }
        catch (JsonException e)
        {
            // The path can carry an unknown member's name verbatim; only the part made of known members is reported.
            throw new PushFormatException($"Not a canonical push at {KnownPath(e.Path)}: unknown member, wrong type or a missing value.");
        }

        // "crossings": [null] reads as a list holding null: refused here rather than failing below (found by ARV-070).
        NoEmptyItem(payload.Tracks, "tracks");
        NoEmptyItem(payload.Crossings, "crossings");
        NoEmptyItem(payload.Occupancy, "occupancy");
        NoEmptyItem(payload.Intervals, "intervals");
        var count = (payload.Tracks?.Count ?? 0) + (payload.Crossings?.Count ?? 0) + (payload.Occupancy?.Count ?? 0) + (payload.Intervals?.Count ?? 0);
        if (count > maxEvents)
            throw new PushFormatException($"At most {maxEvents} events per message.");

        return new MappedPush(
            payload.SentUtc is { } sent ? Utc(sent, "sentUtc") : null,
            payload.PackageId is { } id ? [id] : [],
            [.. (payload.Tracks ?? []).Select(t => new TrackPosition(t.TrackId, Need(t.X, "x"), Need(t.Y, "y"), t.HeightMetres, Utc(t.TimeUtc, "tracks[].timeUtc")))],
            [.. (payload.Crossings ?? []).Select(c => new LineCrossing(c.LineName, c.Direction ?? throw new PushFormatException("crossings[].direction is missing."), c.TrackId, Utc(c.TimeUtc, "crossings[].timeUtc")))],
            [.. (payload.Occupancy ?? []).Select(o => new ZoneOccupancy(o.ZoneName, o.Count ?? throw new PushFormatException("occupancy[].count is missing."), Utc(o.TimeUtc, "occupancy[].timeUtc")))],
            [.. (payload.Intervals ?? []).Select(i => new IntervalCount(i.LineName, i.In ?? 0, i.Out ?? 0, Utc(i.FromUtc, "intervals[].fromUtc"), Utc(i.ToUtc, "intervals[].toUtc")))],
            payload.Status is { } s ? new DeviceStatus(s.Online ?? throw new PushFormatException("status.online is missing."), s.TemperatureCelsius, s.FrameRate, s.ClockOffsetMilliseconds, Utc(s.TimeUtc, "status.timeUtc")) : null,
            0,
            []);
    }

    private static readonly HashSet<string> Members = new(StringComparer.OrdinalIgnoreCase)
    {
        "sentUtc", "packageId", "tracks", "crossings", "occupancy", "intervals", "status", "trackId", "x", "y", "heightMetres", "timeUtc", "lineName",
        "direction", "zoneName", "count", "in", "out", "fromUtc", "toUtc", "online", "temperatureCelsius", "frameRate", "clockOffsetMilliseconds"
    };

    /// <summary>"$.tracks[3].x" as is; the path cut before the first segment that is not a canonical member.</summary>
    private static string KnownPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 200)
            return "$";
        var kept = "$";
        var rest = path.TrimStart('$');
        while (rest.Length > 0)
        {
            if (rest[0] == '.')
            {
                var end = rest.IndexOfAny(['.', '['], 1) is var next and > 0 ? next : rest.Length;
                var name = rest[1..end];
                if (!Members.Contains(name))
                    return kept;
                kept += "." + name;
                rest = rest[end..];
            }
            else if (rest[0] == '[')
            {
                var close = rest.IndexOf(']', StringComparison.Ordinal);
                if (close < 2 || !rest[1..close].All(char.IsAsciiDigit))
                    return kept;
                kept += rest[..(close + 1)];
                rest = rest[(close + 1)..];
            }
            else
            {
                return kept;
            }
        }

        return kept;
    }

    private static DateTime Utc(DateTimeOffset? value, string what)
    {
        if (value is not { } time)
            throw new PushFormatException($"{what} is missing.");
        if (time.Offset != TimeSpan.Zero)
            throw new PushFormatException($"{what} must be UTC (Z or +00:00).");
        return time.UtcDateTime;
    }

    private static void NoEmptyItem<T>(List<T> items, string what) where T : class
    {
        if (items is not null && items.Contains(null))
            throw new PushFormatException($"{what}[] has an empty item.");
    }

    private static double Need(double? value, string what) => value ?? throw new PushFormatException($"tracks[].{what} is missing.");

    private sealed class Payload
    {
        public DateTimeOffset? SentUtc { get; set; }
        public long? PackageId { get; set; }
        public List<Track> Tracks { get; set; }
        public List<Crossing> Crossings { get; set; }
        public List<Occupied> Occupancy { get; set; }
        public List<Interval> Intervals { get; set; }
        public Status Status { get; set; }
    }

    private sealed class Track
    {
        public string TrackId { get; set; }
        public double? X { get; set; }
        public double? Y { get; set; }
        public double? HeightMetres { get; set; }
        public DateTimeOffset? TimeUtc { get; set; }
    }

    private sealed class Crossing
    {
        public string LineName { get; set; }
        public CrossingDirection? Direction { get; set; }
        public string TrackId { get; set; }
        public DateTimeOffset? TimeUtc { get; set; }
    }

    private sealed class Occupied
    {
        public string ZoneName { get; set; }
        public int? Count { get; set; }
        public DateTimeOffset? TimeUtc { get; set; }
    }

    private sealed class Interval
    {
        public string LineName { get; set; }
        public int? In { get; set; }
        public int? Out { get; set; }
        public DateTimeOffset? FromUtc { get; set; }
        public DateTimeOffset? ToUtc { get; set; }
    }

    private sealed class Status
    {
        public bool? Online { get; set; }
        public double? TemperatureCelsius { get; set; }
        public double? FrameRate { get; set; }
        public double? ClockOffsetMilliseconds { get; set; }
        public DateTimeOffset? TimeUtc { get; set; }
    }
}
