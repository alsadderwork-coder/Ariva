using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Sensing;

namespace Ariva.Infra.Sensing;

/// <summary>
/// The Xovis dialect (ARV-023): Xovis data push in JSON, firmware 5 ("package_info.version" 5.0), one envelope or an
/// array of envelopes per POST. Sources and what is verified: docs/architecture/sensor-adapters.md, section "Xovis
/// push format".
/// <list type="bullet">
/// <item><c>logics_data</c>: each logic's records become interval counts on the line named like the logic (counts named
/// fw, forward, in or enter are crossings in, bw, backward, reverse, out or exit crossings out) or, for a logic whose
/// only count is balance or occupancy, the occupancy of the zone named like the logic at the record's end. Logic names
/// are set in the Xovis configuration to the line and zone names of the Ariva zone profile.</item>
/// <item><c>live_data</c>: each frame's tracked people (PERSON, WHEELCHAIR, GROUP) become track positions. Positions are
/// read as metres in the sensor's frame and turned into floor coordinates with the device's registered position and
/// orientation (inferred from the published API, to confirm with Xovis before certification). Tagged objects (staff
/// tags) and other object types (BICYCLE, PRAM) are left out, and so are the scene events, which name geometry by id
/// only.</item>
/// <item><c>connection_test</c>: accepted, nothing else.</item>
/// </list>
/// Legacy firmware 3 and 4 formats and anything else are refused, never guessed.
/// </summary>
public static class XovisPushMapper
{
    private static readonly HashSet<string> InNames = new(StringComparer.OrdinalIgnoreCase) { "fw", "forward", "in", "enter", "entry" };
    private static readonly HashSet<string> OutNames = new(StringComparer.OrdinalIgnoreCase) { "bw", "backward", "reverse", "out", "exit", "leave" };
    private static readonly HashSet<string> OccupancyNames = new(StringComparer.OrdinalIgnoreCase) { "balance", "occupancy" };
    private static readonly HashSet<string> TrackedTypes = new(StringComparer.Ordinal) { "PERSON", "WHEELCHAIR", "GROUP" };

    public static MappedPush Map(JsonElement root, DevicePose pose, int maxEvents)
    {
        ArgumentNullException.ThrowIfNull(pose);
        var envelopes = root.ValueKind switch
        {
            JsonValueKind.Object => [root],
            JsonValueKind.Array when root.GetArrayLength() is > 0 and <= 100 => root.EnumerateArray().ToList(),
            JsonValueKind.Array => throw new PushFormatException("A push carries 1 to 100 envelopes."),
            _ => throw new PushFormatException("A Xovis push is a JSON object or an array of objects.")
        };

        var state = new State(pose, maxEvents);
        foreach (var envelope in envelopes)
        {
            if (envelope.ValueKind != JsonValueKind.Object)
                throw new PushFormatException("A Xovis envelope is a JSON object.");
            var known = false;
            if (PushJson.TryProperty(envelope, "logics_data", out var logics))
            {
                MapLogics(logics, state);
                known = true;
            }

            if (PushJson.TryProperty(envelope, "live_data", out var live))
            {
                MapLive(live, state);
                known = true;
            }

            if (!known && PushJson.TryProperty(envelope, "connection_test", out _))
            {
                state.ConnectionTests++;
                known = true;
            }

            if (!known)
                throw new PushFormatException("Unsupported Xovis envelope: expected logics_data, live_data or connection_test (firmware 5 JSON; legacy formats are not accepted).");
        }

        return new MappedPush(state.SentUtc, state.Packages, state.Tracks, [], state.Occupancy, state.Intervals, null, state.Ignored, [.. state.Notes],
            ConnectionTest: state.ConnectionTests == envelopes.Count);
    }

    private sealed class State(DevicePose pose, int maxEvents)
    {
        public DevicePose Pose { get; } = pose;
        public DateTime? SentUtc { get; set; }
        public List<long> Packages { get; } = [];
        public List<TrackPosition> Tracks { get; } = [];
        public List<ZoneOccupancy> Occupancy { get; } = [];
        public List<IntervalCount> Intervals { get; } = [];
        public int Ignored { get; set; }
        public int ConnectionTests { get; set; }
        public HashSet<string> Notes { get; } = new(StringComparer.Ordinal);

        public void Count()
        {
            if (Tracks.Count + Occupancy.Count + Intervals.Count >= maxEvents)
                throw new PushFormatException($"At most {maxEvents} events per message.");
        }

        public void Ignore(string why)
        {
            Ignored++;
            if (Notes.Count < 10)
                Notes.Add(why);
        }
    }

    private static void Header(JsonElement data, State state, string where)
    {
        if (PushJson.TryProperty(data, "package_info", out var package) && PushJson.Long(package, "id") is { } id)
            state.Packages.Add(id);
        if (PushJson.TryProperty(data, "sensor_info", out var sensor) && PushJson.TryProperty(sensor, "time", out var time))
        {
            var sent = PushJson.Time(time, $"{where}.sensor_info.time");
            state.SentUtc = state.SentUtc is { } earlier && earlier > sent ? earlier : sent;
        }
    }

    private static void MapLogics(JsonElement data, State state)
    {
        if (data.ValueKind != JsonValueKind.Object)
            throw new PushFormatException("logics_data is not an object.");
        Header(data, state, "logics_data");
        foreach (var logic in PushJson.Required(data, "logics", JsonValueKind.Array, "logics_data").EnumerateArray())
        {
            var name = PushJson.Text(logic, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                state.Ignore("a logic without a name");
                continue;
            }

            if (!PushJson.TryProperty(logic, "records", out var records) || records.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var record in records.EnumerateArray())
            {
                var from = PushJson.Time(PushJson.Required(record, "from", RecordTimeKind(record, "from"), "record"), "record.from");
                var to = PushJson.Time(PushJson.Required(record, "to", RecordTimeKind(record, "to"), "record"), "record.to");
                long? countIn = null, countOut = null, balance = null;
                foreach (var count in PushJson.Required(record, "counts", JsonValueKind.Array, "record").EnumerateArray())
                {
                    var countName = PushJson.Text(count, "name") ?? string.Empty;
                    var value = PushJson.TryProperty(count, "value", out var v) ? PushJson.Number(v) : null;
                    if (value is not { } number || number != Math.Floor(number) || number is < int.MinValue or > int.MaxValue)
                    {
                        state.Ignore($"count {PushJson.Quote(countName)} of {PushJson.Quote(name)} without a whole value");
                        continue;
                    }

                    if (InNames.Contains(countName))
                        countIn = (countIn ?? 0) + (long)number;
                    else if (OutNames.Contains(countName))
                        countOut = (countOut ?? 0) + (long)number;
                    else if (OccupancyNames.Contains(countName))
                        balance = (long)number;
                    else
                        state.Ignore($"count {PushJson.Quote(countName)} of {PushJson.Quote(name)} is not an in, out or occupancy count");
                }

                if (countIn is not null || countOut is not null)
                {
                    state.Count();
                    // Sums beyond an int are out of the canonical bounds anyway; clamp so validation, not overflow, refuses them.
                    state.Intervals.Add(new IntervalCount(name, Clamp(countIn ?? 0), Clamp(countOut ?? 0), from, to));
                }
                else if (balance is { } occupancy)
                {
                    state.Count();
                    if (occupancy < 0)
                        state.Ignore($"negative occupancy of {PushJson.Quote(name)} read as 0");
                    state.Occupancy.Add(new ZoneOccupancy(name, Clamp(Math.Max(0, occupancy)), to));
                }
            }
        }
    }

    /// <summary>Record times may be RFC 3339 strings or Unix numbers, depending on the agent's time format.</summary>
    private static JsonValueKind RecordTimeKind(JsonElement record, string name) =>
        PushJson.TryProperty(record, name, out var value) && value.ValueKind == JsonValueKind.Number ? JsonValueKind.Number : JsonValueKind.String;

    private static void MapLive(JsonElement data, State state)
    {
        if (data.ValueKind != JsonValueKind.Object)
            throw new PushFormatException("live_data is not an object.");
        Header(data, state, "live_data");
        foreach (var frame in PushJson.Required(data, "frames", JsonValueKind.Array, "live_data").EnumerateArray())
        {
            if (!PushJson.TryProperty(frame, "time", out var timeValue))
                throw new PushFormatException("live_data.frames[].time is missing.");
            var time = PushJson.Time(timeValue, "frame.time");
            if (PushJson.TryProperty(frame, "events", out var events) && events.ValueKind == JsonValueKind.Array && events.GetArrayLength() > 0)
                state.Ignore("scene events (Ariva computes crossings from tracks)");
            if (!PushJson.TryProperty(frame, "tracked_objects", out var objects) || objects.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var tracked in objects.EnumerateArray())
            {
                var type = PushJson.Text(tracked, "type", 32);
                if (type is null || !TrackedTypes.Contains(type))
                {
                    state.Ignore($"object type {PushJson.Quote(type ?? "none")} is not a person");
                    continue;
                }

                if (PushJson.TryProperty(tracked, "attributes", out var attributes) && PushJson.Text(attributes, "tag", 32) is { } tag &&
                    !string.Equals(tag, "NO_TAG", StringComparison.Ordinal))
                {
                    state.Ignore("tagged objects (staff)");
                    continue;
                }

                var id = PushJson.Text(tracked, "track_id_str", 64) ?? PushJson.Text(tracked, "track_id", 64);
                if (!PushJson.TryProperty(tracked, "position", out var position) || position.ValueKind != JsonValueKind.Array || position.GetArrayLength() < 2)
                    throw new PushFormatException("tracked_objects[].position is not an array of coordinates.");
                var x = PushJson.Number(position[0]);
                var y = PushJson.Number(position[1]);
                var z = position.GetArrayLength() > 2 ? PushJson.Number(position[2]) : null;
                if (x is null || y is null)
                    throw new PushFormatException("tracked_objects[].position holds a value that is not a finite number.");

                var (floorX, floorY) = state.Pose.ToFloor(x.Value, y.Value);
                state.Count();
                state.Tracks.Add(new TrackPosition(id, floorX, floorY, z is { } height and >= 0 and <= 3 ? height : null, time));
            }
        }
    }

    private static int Clamp(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);

    /// <summary>The dialect this mapper serves.</summary>
    public const DeviceDialect Dialect = DeviceDialect.Xovis;
}
