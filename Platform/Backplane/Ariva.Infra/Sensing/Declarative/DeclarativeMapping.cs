using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Ariva.Infra.Sensing.Declarative;

/// <summary>
/// A declarative mapping (ARV-024): how a vendor's JSON payload becomes canonical events, written as data. A mapping is
/// a JSON document shipped with Ariva (Ariva.Infra/Sensing/Mappings, reviewed like code) and parsed and checked once at
/// start-up; a device names it. It holds only restricted paths (<see cref="RestrictedPath"/>), equality filters, time
/// units and a coordinate scale: nothing in it is evaluated as an expression, compiled or executed (CWE-94).
/// <code>
/// { "name": "ouster-detect-v1", "title": "...", "source": "https://...",
///   "positions": { "frame": "device", "scale": 1 },
///   "sentTime": { "path": "$.sent", "unit": "rfc3339" },  "package": "$.sequence",
///   "tracks":    { "groups": "$.frames[*]", "items": "^.objects[*]", "where": [{ "path": "@.class", "equals": ["PERSON"] }],
///                  "trackId": "@.id", "x": "@.position.x", "y": "@.position.y", "height": "@.height", "time": { "path": "^.timestamp", "unit": "us" } },
///   "crossings": { "items": "$.events[*]", "line": "@.line", "direction": { "path": "@.dir", "in": ["IN"], "out": ["OUT"] }, "trackId": "@.id", "time": { ... } },
///   "occupancy": { "items": "$.zones[*]", "zone": "@.name", "count": "@.count", "time": { "received": true } },
///   "intervals": { "items": "$.counts[*]", "line": "@.line", "in": "@.in", "out": "@.out", "from": { ... }, "to": { ... } } }
/// </code>
/// A section enumerates items: <c>items</c> from the payload (<c>$</c>) or, with <c>groups</c>, from each group
/// (<c>^</c>); fields are read from the item (<c>@</c>), its group (<c>^</c>) or the payload (<c>$</c>). Items that do
/// not pass every <c>where</c> are ignored on purpose. Positions are metres times <c>scale</c>, in floor coordinates
/// (<c>floor</c>) or in the device's own frame (<c>device</c>, placed with its registered position and orientation).
/// A time is RFC 3339 with an offset or a Unix time in s, ms, us or ns; <c>received</c> stamps events with Ariva's
/// receipt time for payloads that carry none (no clock correction then).
/// </summary>
public sealed partial class DeclarativeMapping
{
    public const int MaxDocumentBytes = 32 * 1024;
    public const int MaxFilters = 4;
    public const int MaxFilterValues = 16;
    public const int MaxFilterValueLength = 64;
    public const double MaxScale = 1_000;

    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 8
    };

    private DeclarativeMapping()
    {
    }

    public string Name { get; private init; }
    public string Title { get; private init; }
    public string Source { get; private init; }
    public bool DeviceFrame { get; private init; }
    public double Scale { get; private init; } = 1;
    public TimeField SentTime { get; private init; }
    public RestrictedPath Package { get; private init; }
    public TrackSection Tracks { get; private init; }
    public CrossingSection Crossings { get; private init; }
    public OccupancySection Occupancy { get; private init; }
    public IntervalSection Intervals { get; private init; }

    /// <summary>Parses and checks a mapping document; <paramref name="expectedName"/> must be its name (the file it came from).</summary>
    public static DeclarativeMapping Parse(ReadOnlySpan<byte> utf8, string expectedName)
    {
        if (utf8.Length is 0 or > MaxDocumentBytes)
            throw new MappingException($"A mapping is 1 byte to {MaxDocumentBytes / 1024} KB.");
        Spec spec;
        try
        {
            spec = JsonSerializer.Deserialize<Spec>(utf8, Strict) ?? throw new MappingException("The mapping is empty.");
        }
        catch (JsonException e)
        {
            throw new MappingException($"The mapping is not valid at {e.Path ?? "$"}: unknown member, wrong type or malformed JSON.");
        }

        if (spec.Name is null || !NamePattern().IsMatch(spec.Name))
            throw new MappingException("name: lower case letters, digits and hyphens, at most 64.");
        if (expectedName is not null && !string.Equals(spec.Name, expectedName, StringComparison.Ordinal))
            throw new MappingException($"name: '{spec.Name}' is not the expected '{expectedName}'.");
        var where = spec.Name;
        if (string.IsNullOrWhiteSpace(spec.Title) || spec.Title.Length > 200)
            throw new MappingException($"{where}.title: 1 to 200 characters.");
        if (spec.Source is not null && (spec.Source.Length > 300 || !spec.Source.StartsWith("https://", StringComparison.Ordinal)))
            throw new MappingException($"{where}.source: an https link of at most 300 characters.");

        var frame = spec.Positions?.Frame ?? "floor";
        if (frame is not ("floor" or "device"))
            throw new MappingException($"{where}.positions.frame: floor or device.");
        var scale = spec.Positions?.Scale ?? 1;
        if (!double.IsFinite(scale) || scale <= 0 || scale > MaxScale)
            throw new MappingException($"{where}.positions.scale: above 0 and at most {MaxScale}.");

        var mapping = new DeclarativeMapping
        {
            Name = spec.Name,
            Title = spec.Title,
            Source = spec.Source,
            DeviceFrame = frame == "device",
            Scale = scale,
            SentTime = spec.SentTime is null ? null : TimeField.From(spec.SentTime, $"{where}.sentTime", payloadOnly: true, grouped: false),
            Package = spec.Package is null ? null : PayloadPath(spec.Package, $"{where}.package"),
            Tracks = spec.Tracks is null ? null : TrackSection.From(spec.Tracks, $"{where}.tracks"),
            Crossings = spec.Crossings is null ? null : CrossingSection.From(spec.Crossings, $"{where}.crossings"),
            Occupancy = spec.Occupancy is null ? null : OccupancySection.From(spec.Occupancy, $"{where}.occupancy"),
            Intervals = spec.Intervals is null ? null : IntervalSection.Create(spec.Intervals, $"{where}.intervals")
        };
        if (mapping.Tracks is null && mapping.Crossings is null && mapping.Occupancy is null && mapping.Intervals is null)
            throw new MappingException($"{where}: at least one of tracks, crossings, occupancy or intervals.");
        if (mapping.SentTime is { Received: true })
            throw new MappingException($"{where}.sentTime: a path, not received (the receipt time says nothing about the device's clock).");
        return mapping;
    }

    private static RestrictedPath PayloadPath(string text, string where)
    {
        var path = RestrictedPath.Parse(text, allowWildcard: false, where);
        if (path.Root != '$')
            throw new MappingException($"{where}: a path from the payload ($).");
        return path;
    }

    /// <summary>A field read from the item, its group or the payload; never a wildcard.</summary>
    private static RestrictedPath Field(string text, string where, bool grouped, bool required)
    {
        if (text is null)
        {
            if (required)
                throw new MappingException($"{where}: required.");
            return null;
        }

        var path = RestrictedPath.Parse(text, allowWildcard: false, where);
        if (path.Root == '^' && !grouped)
            throw new MappingException($"{where}: ^ (the group) is only for sections with groups.");
        return path;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex NamePattern();

    /// <summary>The unit a time is given in.</summary>
    public enum TimeUnit
    {
        Rfc3339,
        Seconds,
        Milliseconds,
        Microseconds,
        Nanoseconds
    }

    /// <summary>Where an event's time comes from: a path and its unit, or Ariva's receipt time.</summary>
    public sealed class TimeField
    {
        public RestrictedPath Path { get; private init; }
        public TimeUnit Unit { get; private init; }
        public bool Received { get; private init; }

        internal static TimeField From(TimeSpec spec, string where, bool payloadOnly, bool grouped)
        {
            if (spec.Received == true)
            {
                if (spec.Path is not null || spec.Unit is not null)
                    throw new MappingException($"{where}: either received or a path with its unit.");
                return new TimeField { Received = true };
            }

            var path = payloadOnly ? PayloadPath(spec.Path, $"{where}.path") : Field(spec.Path, $"{where}.path", grouped, required: true);
            var unit = spec.Unit switch
            {
                "rfc3339" => TimeUnit.Rfc3339,
                "s" => TimeUnit.Seconds,
                "ms" => TimeUnit.Milliseconds,
                "us" => TimeUnit.Microseconds,
                "ns" => TimeUnit.Nanoseconds,
                _ => throw new MappingException($"{where}.unit: rfc3339, s, ms, us or ns.")
            };
            return new TimeField { Path = path, Unit = unit };
        }
    }

    /// <summary>An item is kept only when the value at <see cref="Path"/> is one of <see cref="Values"/> (strings, numbers or booleans as written).</summary>
    public sealed record Filter(RestrictedPath Path, IReadOnlySet<string> Values);

    /// <summary>What every section has: the items to enumerate, optionally within groups, and the filters they must pass.</summary>
    public abstract class Section
    {
        public RestrictedPath Groups { get; private set; }
        public RestrictedPath Items { get; private set; }
        public IReadOnlyList<Filter> Where { get; private set; } = [];
        public bool Grouped => Groups is not null;

        private protected void Enumerate(SectionSpec spec, string where)
        {
            if (spec.Groups is not null)
            {
                Groups = RestrictedPath.Parse(spec.Groups, allowWildcard: true, $"{where}.groups");
                if (Groups.Root != '$' || !Groups.HasWildcard)
                    throw new MappingException($"{where}.groups: a path from the payload ($) with one [*].");
            }

            Items = RestrictedPath.Parse(spec.Items, allowWildcard: true, $"{where}.items");
            if (!Items.HasWildcard)
                throw new MappingException($"{where}.items: one [*] to enumerate.");
            if (Items.Root != (Grouped ? '^' : '$'))
                throw new MappingException($"{where}.items: from the group (^) when there are groups, from the payload ($) otherwise.");

            var filters = spec.Where ?? [];
            if (filters.Count > MaxFilters)
                throw new MappingException($"{where}.where: at most {MaxFilters} filters.");
            var list = new List<Filter>();
            for (var i = 0; i < filters.Count; i++)
            {
                var f = filters[i] ?? throw new MappingException($"{where}.where[{i}]: an object.");
                var values = f.EqualsAny ?? [];
                if (values.Count is 0 or > MaxFilterValues || values.Any(v => string.IsNullOrEmpty(v) || v.Length > MaxFilterValueLength))
                    throw new MappingException($"{where}.where[{i}].equals: 1 to {MaxFilterValues} values of 1 to {MaxFilterValueLength} characters.");
                list.Add(new Filter(Field(f.Path, $"{where}.where[{i}].path", Grouped, required: true), new HashSet<string>(values, StringComparer.Ordinal)));
            }

            Where = list;
        }

        private protected RestrictedPath Read(string text, string where, bool required = true) => Field(text, where, Grouped, required);

        private protected TimeField ReadTime(TimeSpec spec, string where) =>
            spec is null ? throw new MappingException($"{where}: required.") : TimeField.From(spec, where, payloadOnly: false, Grouped);
    }

    public sealed class TrackSection : Section
    {
        public RestrictedPath TrackId { get; private set; }
        public RestrictedPath X { get; private set; }
        public RestrictedPath Y { get; private set; }
        public RestrictedPath Height { get; private set; }
        public TimeField Time { get; private set; }

        internal static TrackSection From(TrackSpec spec, string where)
        {
            var section = new TrackSection();
            section.Enumerate(spec, where);
            section.TrackId = section.Read(spec.TrackId, $"{where}.trackId");
            section.X = section.Read(spec.X, $"{where}.x");
            section.Y = section.Read(spec.Y, $"{where}.y");
            section.Height = section.Read(spec.Height, $"{where}.height", required: false);
            section.Time = section.ReadTime(spec.Time, $"{where}.time");
            return section;
        }
    }

    public sealed class CrossingSection : Section
    {
        public RestrictedPath Line { get; private set; }
        public RestrictedPath Direction { get; private set; }
        public IReadOnlySet<string> In { get; private set; }
        public IReadOnlySet<string> Out { get; private set; }
        public RestrictedPath TrackId { get; private set; }
        public TimeField Time { get; private set; }

        internal static CrossingSection From(CrossingSpec spec, string where)
        {
            var section = new CrossingSection();
            section.Enumerate(spec, where);
            section.Line = section.Read(spec.Line, $"{where}.line");
            var direction = spec.Direction ?? throw new MappingException($"{where}.direction: required.");
            section.Direction = section.Read(direction.Path, $"{where}.direction.path");
            var (inValues, outValues) = (direction.In ?? [], direction.Out ?? []);
            if (inValues.Count is 0 or > MaxFilterValues || outValues.Count is 0 or > MaxFilterValues ||
                inValues.Concat(outValues).Any(v => string.IsNullOrEmpty(v) || v.Length > MaxFilterValueLength))
                throw new MappingException($"{where}.direction: in and out are 1 to {MaxFilterValues} values of 1 to {MaxFilterValueLength} characters.");
            if (inValues.Intersect(outValues, StringComparer.Ordinal).Any())
                throw new MappingException($"{where}.direction: a value cannot mean both in and out.");
            section.In = new HashSet<string>(inValues, StringComparer.Ordinal);
            section.Out = new HashSet<string>(outValues, StringComparer.Ordinal);
            section.TrackId = section.Read(spec.TrackId, $"{where}.trackId", required: false);
            section.Time = section.ReadTime(spec.Time, $"{where}.time");
            return section;
        }
    }

    public sealed class OccupancySection : Section
    {
        public RestrictedPath Zone { get; private set; }
        public RestrictedPath Count { get; private set; }
        public TimeField Time { get; private set; }

        internal static OccupancySection From(OccupancySpec spec, string where)
        {
            var section = new OccupancySection();
            section.Enumerate(spec, where);
            section.Zone = section.Read(spec.Zone, $"{where}.zone");
            section.Count = section.Read(spec.Count, $"{where}.count");
            section.Time = section.ReadTime(spec.Time, $"{where}.time");
            return section;
        }
    }

    public sealed class IntervalSection : Section
    {
        public RestrictedPath Line { get; private set; }
        public RestrictedPath In { get; private set; }
        public RestrictedPath Out { get; private set; }
        public TimeField From { get; private set; }
        public TimeField To { get; private set; }

        internal static IntervalSection Create(IntervalSpec spec, string where)
        {
            var section = new IntervalSection();
            section.Enumerate(spec, where);
            section.Line = section.Read(spec.Line, $"{where}.line");
            section.In = section.Read(spec.In, $"{where}.in", required: false);
            section.Out = section.Read(spec.Out, $"{where}.out", required: false);
            if (section.In is null && section.Out is null)
                throw new MappingException($"{where}: in, out or both.");
            section.From = section.ReadTime(spec.From, $"{where}.from");
            section.To = section.ReadTime(spec.To, $"{where}.to");
            if (section.From.Received || section.To.Received)
                throw new MappingException($"{where}: an interval's from and to come from the payload.");
            return section;
        }
    }

    // The document as written; every member is optional here and checked above, so the messages say what is wrong.
    private sealed class Spec
    {
        public string Name { get; set; }
        public string Title { get; set; }
        public string Source { get; set; }
        public PositionsSpec Positions { get; set; }
        public TimeSpec SentTime { get; set; }
        public string Package { get; set; }
        public TrackSpec Tracks { get; set; }
        public CrossingSpec Crossings { get; set; }
        public OccupancySpec Occupancy { get; set; }
        public IntervalSpec Intervals { get; set; }
    }

    private sealed class PositionsSpec
    {
        public string Frame { get; set; }
        public double? Scale { get; set; }
    }

    internal sealed class TimeSpec
    {
        public string Path { get; set; }
        public string Unit { get; set; }
        public bool? Received { get; set; }
    }

    internal sealed class FilterSpec
    {
        public string Path { get; set; }

        [JsonPropertyName("equals")]
        public List<string> EqualsAny { get; set; }
    }

    internal abstract class SectionSpec
    {
        public string Groups { get; set; }
        public string Items { get; set; }
        public List<FilterSpec> Where { get; set; }
    }

    internal sealed class TrackSpec : SectionSpec
    {
        public string TrackId { get; set; }
        public string X { get; set; }
        public string Y { get; set; }
        public string Height { get; set; }
        public TimeSpec Time { get; set; }
    }

    internal sealed class DirectionSpec
    {
        public string Path { get; set; }
        public List<string> In { get; set; }
        public List<string> Out { get; set; }
    }

    internal sealed class CrossingSpec : SectionSpec
    {
        public string Line { get; set; }
        public DirectionSpec Direction { get; set; }
        public string TrackId { get; set; }
        public TimeSpec Time { get; set; }
    }

    internal sealed class OccupancySpec : SectionSpec
    {
        public string Zone { get; set; }
        public string Count { get; set; }
        public TimeSpec Time { get; set; }
    }

    internal sealed class IntervalSpec : SectionSpec
    {
        public string Line { get; set; }
        public string In { get; set; }
        public string Out { get; set; }
        public TimeSpec From { get; set; }
        public TimeSpec To { get; set; }
    }
}
