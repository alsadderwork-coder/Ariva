using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Sensing;

namespace Ariva.Infra.Sensing.Declarative;

/// <summary>
/// The declarative dialect (ARV-024): a payload read through a <see cref="DeclarativeMapping"/>. Only the mapping's
/// restricted paths are walked; values are type-checked (strings or integers for ids and names, finite numbers for
/// positions, integers for counts, times in the mapping's unit), and a required value that is missing or of the wrong
/// type refuses the whole message, as in the coded dialects. Items that fail a filter, or whose direction the mapping
/// does not name, are ignored on purpose and counted. Work is bounded: at most four times the event limit of items are
/// looked at, and at most the event limit become events.
/// </summary>
public static class DeclarativeMapper
{
    public const DeviceDialect Dialect = DeviceDialect.Declarative;

    /// <summary>Items looked at (kept or filtered out) per message, as a multiple of the event limit.</summary>
    public const int WorkFactor = 4;

    private static readonly DateTime Earliest = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Latest = new(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static MappedPush Map(JsonElement root, DeclarativeMapping mapping, DevicePose pose, int maxEvents, DateTime receivedUtc)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(pose);
        if (root.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            throw new PushFormatException("A push is a JSON object or array.");

        var state = new State(root, mapping, pose, maxEvents, receivedUtc);
        DateTime? sent = null;
        if (mapping.SentTime is { } sentTime && sentTime.Path.Single(root, default, default) is { } sentValue)
            sent = ToTime(sentValue, sentTime.Unit, "sentTime");
        var packages = new List<long>();
        if (mapping.Package?.Single(root, default, default) is { } package)
        {
            if (package.ValueKind != JsonValueKind.Number || !package.TryGetInt64(out var id))
                throw new PushFormatException($"package ({mapping.Package}) is not an integer.");
            packages.Add(id);
        }

        var receiptTimed = PushKinds.None;
        if (mapping.Tracks is { } tracks)
        {
            state.Each(tracks, "tracks", (group, item, i) =>
            {
                var id = state.Id(tracks.TrackId, group, item, $"tracks[{i}].trackId");
                var (x, y) = state.Position(tracks.X, tracks.Y, group, item, i);
                var height = tracks.Height is null ? null : state.OptionalNumber(tracks.Height, group, item, $"tracks[{i}].height") * mapping.Scale;
                state.Tracks.Add(new TrackPosition(id, x, y, height, state.Time(tracks.Time, group, item, $"tracks[{i}].time")));
            });
            receiptTimed |= tracks.Time.Received ? PushKinds.Tracks : PushKinds.None;
        }

        if (mapping.Crossings is { } crossings)
        {
            state.Each(crossings, "crossings", (group, item, i) =>
            {
                var direction = Scalar(state.Value(crossings.Direction, group, item));
                CrossingDirection? mapped = direction is null ? null
                    : crossings.In.Contains(direction) ? CrossingDirection.In
                    : crossings.Out.Contains(direction) ? CrossingDirection.Out
                    : null;
                if (mapped is null)
                {
                    state.Ignore("crossings: a direction the mapping does not name");
                    return;
                }

                state.Crossings.Add(new LineCrossing(
                    state.Name(crossings.Line, group, item, $"crossings[{i}].line"),
                    mapped.Value,
                    crossings.TrackId is null ? null : state.OptionalId(crossings.TrackId, group, item, $"crossings[{i}].trackId"),
                    state.Time(crossings.Time, group, item, $"crossings[{i}].time")));
            });
            receiptTimed |= crossings.Time.Received ? PushKinds.Crossings : PushKinds.None;
        }

        if (mapping.Occupancy is { } occupancy)
        {
            state.Each(occupancy, "occupancy", (group, item, i) => state.Occupancy.Add(new ZoneOccupancy(
                state.Name(occupancy.Zone, group, item, $"occupancy[{i}].zone"),
                state.Integer(occupancy.Count, group, item, $"occupancy[{i}].count"),
                state.Time(occupancy.Time, group, item, $"occupancy[{i}].time"))));
            receiptTimed |= occupancy.Time.Received ? PushKinds.Occupancy : PushKinds.None;
        }

        if (mapping.Intervals is { } intervals)
        {
            state.Each(intervals, "intervals", (group, item, i) => state.Intervals.Add(new IntervalCount(
                state.Name(intervals.Line, group, item, $"intervals[{i}].line"),
                intervals.In is null ? 0 : state.Integer(intervals.In, group, item, $"intervals[{i}].in"),
                intervals.Out is null ? 0 : state.Integer(intervals.Out, group, item, $"intervals[{i}].out"),
                state.Time(intervals.From, group, item, $"intervals[{i}].from"),
                state.Time(intervals.To, group, item, $"intervals[{i}].to"))));
        }

        return new MappedPush(sent, packages, state.Tracks, state.Crossings, state.Occupancy, state.Intervals, null, state.Ignored, [.. state.Notes],
            ReceiptTimed: receiptTimed);
    }

    /// <summary>A string as written, a number as written, true or false; null for anything else.</summary>
    internal static string Scalar(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { ValueKind: JsonValueKind.Number } v => v.GetRawText(),
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        _ => null
    };

    /// <summary>A time in the mapping's unit, between 2000 and 2100.</summary>
    internal static DateTime ToTime(JsonElement value, DeclarativeMapping.TimeUnit unit, string what)
    {
        DateTime time;
        if (unit == DeclarativeMapping.TimeUnit.Rfc3339)
        {
            if (value.ValueKind != JsonValueKind.String)
                throw new PushFormatException($"{what} is not an RFC 3339 time.");
            time = PushJson.Time(value, what);
        }
        else
        {
            if (value.ValueKind != JsonValueKind.Number)
                throw new PushFormatException($"{what} is not a number.");
            long ticks;
            try
            {
                if (value.TryGetInt64(out var whole))
                {
                    ticks = unit switch
                    {
                        DeclarativeMapping.TimeUnit.Seconds => checked(whole * TimeSpan.TicksPerSecond),
                        DeclarativeMapping.TimeUnit.Milliseconds => checked(whole * TimeSpan.TicksPerMillisecond),
                        DeclarativeMapping.TimeUnit.Microseconds => checked(whole * 10),
                        _ => whole / 100
                    };
                }
                else if (value.TryGetDouble(out var fraction) && double.IsFinite(fraction) && Math.Abs(fraction) < 1e13)
                {
                    // Fractional Unix times (seconds with a decimal part, as some platforms send); whole units are exact above.
                    var perUnit = unit switch
                    {
                        DeclarativeMapping.TimeUnit.Seconds => TimeSpan.TicksPerSecond,
                        DeclarativeMapping.TimeUnit.Milliseconds => TimeSpan.TicksPerMillisecond,
                        DeclarativeMapping.TimeUnit.Microseconds => 10.0,
                        _ => 0.01
                    };
                    ticks = (long)Math.Round(fraction * perUnit);
                }
                else
                {
                    throw new PushFormatException($"{what} is out of range.");
                }
            }
            catch (OverflowException)
            {
                throw new PushFormatException($"{what} is out of range.");
            }

            if (ticks < (Earliest - DateTime.UnixEpoch).Ticks || ticks > (Latest - DateTime.UnixEpoch).Ticks)
                throw new PushFormatException($"{what} is out of range.");
            time = DateTime.UnixEpoch.AddTicks(ticks);
        }

        if (time < Earliest || time > Latest)
            throw new PushFormatException($"{what} is out of range.");
        return DateTime.SpecifyKind(time, DateTimeKind.Utc);
    }

    private sealed class State(JsonElement root, DeclarativeMapping mapping, DevicePose pose, int maxEvents, DateTime receivedUtc)
    {
        private readonly HashSet<string> _notes = new(StringComparer.Ordinal);
        private readonly Dictionary<RestrictedPath, JsonElement?> _perGroup = new(ReferenceEqualityComparer.Instance);
        private int _looked;

        /// <summary>
        /// The value at a path for this item. Values from the group (^) or the payload (<c>$</c>) are read once per group:
        /// looking a member up walks the object's members, so re-reading a wide group object for every item would cost
        /// items times members.
        /// </summary>
        public JsonElement? Value(RestrictedPath path, JsonElement group, JsonElement item)
        {
            if (path.Root == '@')
                return path.Single(root, group, item);
            if (!_perGroup.TryGetValue(path, out var value))
                _perGroup[path] = value = path.Single(root, group, item);
            return value;
        }

        public List<TrackPosition> Tracks { get; } = [];
        public List<LineCrossing> Crossings { get; } = [];
        public List<ZoneOccupancy> Occupancy { get; } = [];
        public List<IntervalCount> Intervals { get; } = [];
        public int Ignored { get; private set; }
        public IEnumerable<string> Notes => _notes;

        private int Events => Tracks.Count + Crossings.Count + Occupancy.Count + Intervals.Count;

        public void Ignore(string note)
        {
            Ignored++;
            if (_notes.Count < 5)
                _notes.Add(note);
        }

        /// <summary>Every item of the section that passes its filters, with its group and its index among the kept ones.</summary>
        public void Each(DeclarativeMapping.Section section, string kind, Action<JsonElement, JsonElement, int> map)
        {
            var budget = Math.Max(1, maxEvents) * WorkFactor;
            IReadOnlyList<JsonElement> groups = section.Grouped ? section.Groups.Many(root, default, budget) : [default];
            if (groups.Count > budget)
                throw new PushFormatException($"At most {budget} {kind} groups per message.");
            var index = 0;
            foreach (var group in groups)
            {
                _perGroup.Clear();
                var items = section.Items.Many(root, group, budget - _looked);
                _looked += items.Count;
                if (_looked > budget)
                    throw new PushFormatException($"At most {budget} items per message.");
                foreach (var item in items)
                {
                    if (!section.Where.All(f => Scalar(Value(f.Path, group, item)) is { } value && f.Values.Contains(value)))
                    {
                        Ignore($"{kind}: items the mapping's filters leave out");
                        continue;
                    }

                    var before = Events;
                    map(group, item, index);
                    if (Events > before)
                        index++;
                    if (Events > maxEvents)
                        throw new PushFormatException($"At most {maxEvents} events per message.");
                }
            }
        }

        private JsonElement Need(RestrictedPath path, JsonElement group, JsonElement item, string what) =>
            Value(path, group, item) ?? throw new PushFormatException($"{what} is missing ({path}).");

        /// <summary>An id: a string, or an integer written as a number.</summary>
        public string Id(RestrictedPath path, JsonElement group, JsonElement item, string what)
        {
            var value = Need(path, group, item, what);
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number when value.TryGetInt64(out var number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => throw new PushFormatException($"{what} is not a string or an integer ({path}).")
            };
        }

        public string OptionalId(RestrictedPath path, JsonElement group, JsonElement item, string what) =>
            Value(path, group, item) is null ? null : Id(path, group, item, what);

        /// <summary>A line or zone name: a string (names are checked against the published geometry later).</summary>
        public string Name(RestrictedPath path, JsonElement group, JsonElement item, string what)
        {
            var value = Need(path, group, item, what);
            return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new PushFormatException($"{what} is not a string ({path}).");
        }

        public int Integer(RestrictedPath path, JsonElement group, JsonElement item, string what)
        {
            var value = Need(path, group, item, what);
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
                ? number
                : throw new PushFormatException($"{what} is not an integer ({path}).");
        }

        private double Number(RestrictedPath path, JsonElement group, JsonElement item, string what) =>
            PushJson.Number(Need(path, group, item, what)) ?? throw new PushFormatException($"{what} is not a finite number ({path}).");

        public double? OptionalNumber(RestrictedPath path, JsonElement group, JsonElement item, string what) =>
            Value(path, group, item) is { } value
                ? PushJson.Number(value) ?? throw new PushFormatException($"{what} is not a finite number ({path}).")
                : null;

        /// <summary>A position in metres on the device's level: scaled, and placed with the device's pose when given in its own frame.</summary>
        public (double X, double Y) Position(RestrictedPath x, RestrictedPath y, JsonElement group, JsonElement item, int i)
        {
            var (localX, localY) = (Number(x, group, item, $"tracks[{i}].x") * mapping.Scale, Number(y, group, item, $"tracks[{i}].y") * mapping.Scale);
            return mapping.DeviceFrame ? pose.ToFloor(localX, localY) : (localX, localY);
        }

        public DateTime Time(DeclarativeMapping.TimeField field, JsonElement group, JsonElement item, string what) =>
            field.Received ? receivedUtc : ToTime(Need(field.Path, group, item, what), field.Unit, what);
    }
}
