using System.Text;
using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Sensing;
using Ariva.Infra.Sensing;
using Ariva.Infra.Sensing.Declarative;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Sensing;

/// <summary>
/// ARV-024 conformance: every sample under Sensing/Samples/declarative/&lt;mapping&gt; maps, through that shipped mapping,
/// to exactly its expected canonical events; a mapping covering every section reads crossings, intervals, send times,
/// packages and unit conversions; missing or mistyped values refuse the message without echoing the payload; the event
/// and work limits hold.
/// </summary>
public sealed class DeclarativeConformanceTests
{
    private static readonly DateTime Received = new(2026, 10, 2, 14, 5, 1, DateTimeKind.Utc);

    private static string Samples => RepositoryPaths.Resolve("Platform/Backplane/Ariva.UnitTests/Sensing/Samples/declarative");

    public static TheoryData<string, string> SampleNames()
    {
        var data = new TheoryData<string, string>();
        foreach (var directory in Directory.GetDirectories(Samples).Order(StringComparer.Ordinal))
        {
            foreach (var file in Directory.GetFiles(Path.Combine(directory, "expected"), "*.json").Order(StringComparer.Ordinal))
                data.Add(Path.GetFileName(directory), Path.GetFileNameWithoutExtension(file));
        }

        return data;
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static DateTime Utc(JsonElement e) => e.GetString() == "received"
        ? Received
        : DateTimeOffset.Parse(e.GetString()!, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime;

    [Fact]
    public void Samples_Should_CoverEveryShippedMapping()
    {
        Directory.GetDirectories(Samples).Select(Path.GetFileName).Should().BeEquivalentTo(DeclarativeMappingCatalog.Embedded.Mappings.Select(m => m.Name),
            "a mapping ships only with a documented sample and its expected events");
    }

    /// <summary>
    /// The kit's warnings for <paramref name="mappings"/> (ARV-174): a mapping whose crossings are stamped with the receipt
    /// time and that names no package gives every message a new batch id, so a resent message is counted again. A warning,
    /// not a failure: such a mapping may be the best a vendor format allows, and the residual is in the mapping guidance.
    /// </summary>
    internal static IReadOnlyList<string> ConformanceWarnings(IEnumerable<DeclarativeMapping> mappings) => [.. mappings.SelectMany(m => m.Warnings)];

    [Fact]
    public void Mappings_Should_BeWarnedAbout_When_TheirCrossingsHaveNeitherAnEventTimeNorAPackage()
    {
        var shipped = DeclarativeMappingCatalog.Embedded.Mappings.Select(m => DeclarativeMappingCatalog.Embedded.Find(m.Name)).ToList();
        foreach (var warning in ConformanceWarnings(shipped))
            TestContext.Current.AddWarning(warning);

        // The kit does warn: a mapping of receipt-timed crossings without a package is named, with what to do.
        var bare = DeclarativeMapping.Parse(Encoding.UTF8.GetBytes("""
            { "name": "bare-v1", "title": "Receipt-timed crossings", "crossings": { "items": "$.events[*]", "line": "@.line",
              "direction": { "path": "@.dir", "in": ["in"], "out": ["out"] }, "time": { "received": true } } }
            """), "bare-v1");
        ConformanceWarnings([.. shipped, bare]).Should().ContainSingle().Which.Should().StartWith("bare-v1: crossings are stamped with the receipt time")
            .And.Contain("\"package\"");
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void Map_Should_ProduceTheExpectedCanonicalEvents_When_GivenASample(string mappingName, string sample)
    {
        // The theory data are directory and file names under Samples already; GetFileName keeps every read inside it (CWE-22).
        var folder = Path.GetFileName(mappingName);
        var file = Path.GetFileName(sample) + ".json";
        var expected = Parse(File.ReadAllText(Path.Combine(Samples, folder, "expected", file)));
        var pose = expected.GetProperty("pose");
        var mapping = DeclarativeMappingCatalog.Embedded.Find(mappingName);

        var mapped = DeclarativeMapper.Map(Parse(File.ReadAllText(Path.Combine(Samples, folder, file))), mapping,
            new DevicePose(pose[0].GetDouble(), pose[1].GetDouble(), pose[2].GetDouble()), 2_000, Received);

        if (expected.GetProperty("sentUtc").ValueKind == JsonValueKind.Null)
            mapped.DeviceSentUtc.Should().BeNull();
        else
            mapped.DeviceSentUtc.Should().Be(Utc(expected.GetProperty("sentUtc")));
        mapped.PackageIds.Should().Equal(expected.GetProperty("packages").EnumerateArray().Select(p => p.GetInt64()));
        mapped.Ignored.Should().Be(expected.GetProperty("ignored").GetInt32());
        var receiptTimed = expected.GetProperty("receiptTimed").EnumerateArray().Aggregate(PushKinds.None, (k, e) => k | Enum.Parse<PushKinds>(e.GetString()!, ignoreCase: true));
        mapped.ReceiptTimed.Should().Be(receiptTimed);
        mapped.Occupancy.Should().Equal(expected.GetProperty("occupancy").EnumerateArray().Select(o =>
            new ZoneOccupancy(o.GetProperty("zone").GetString(), o.GetProperty("count").GetInt32(), Utc(o.GetProperty("timeUtc")))));
        mapped.Crossings.Should().Equal(expected.GetProperty("crossings").EnumerateArray().Select(c =>
            new LineCrossing(c.GetProperty("line").GetString(), Enum.Parse<CrossingDirection>(c.GetProperty("direction").GetString()!), c.GetProperty("trackId").GetString(),
                Utc(c.GetProperty("timeUtc")))));
        mapped.Intervals.Should().Equal(expected.GetProperty("intervals").EnumerateArray().Select(i =>
            new IntervalCount(i.GetProperty("line").GetString(), i.GetProperty("in").GetInt32(), i.GetProperty("out").GetInt32(), Utc(i.GetProperty("fromUtc")), Utc(i.GetProperty("toUtc")))));
        var tracks = expected.GetProperty("tracks").EnumerateArray().ToList();
        mapped.Tracks.Should().HaveCount(tracks.Count);
        for (var i = 0; i < tracks.Count; i++)
        {
            var t = mapped.Tracks[i];
            t.TrackId.Should().Be(tracks[i].GetProperty("id").GetString());
            t.X.Should().BeApproximately(tracks[i].GetProperty("x").GetDouble(), 1e-9);
            t.Y.Should().BeApproximately(tracks[i].GetProperty("y").GetDouble(), 1e-9);
            t.HeightMetres.Should().Be(tracks[i].GetProperty("height").GetDouble());
            t.TimeUtc.Should().Be(Utc(tracks[i].GetProperty("timeUtc")));
        }

        mapped.Intervals.Concat<CanonicalEvent>(mapped.Occupancy).Concat(mapped.Tracks).Concat(mapped.Crossings)
            .Where(e => CanonicalEventRules.Validate(e).Count > 0).Should().BeEmpty("every mapped event is within the canonical bounds");
    }

    /// <summary>A mapping with every section: floor positions in millimetres, nanosecond track times, a send time and a package.</summary>
    private static readonly DeclarativeMapping Full = DeclarativeMapping.Parse(Encoding.UTF8.GetBytes("""
        { "name": "full-v1", "title": "Every section", "source": "https://example.com/spec",
          "positions": { "frame": "floor", "scale": 0.001 },
          "sentTime": { "path": "$.header.sent", "unit": "rfc3339" }, "package": "$.header.seq",
          "tracks": { "items": "$.objects[*]", "where": [{ "path": "@.kind", "equals": ["person", "1"] }],
                      "trackId": "@.uid", "x": "@.p[0]", "y": "@.p[1]", "time": { "path": "@.t", "unit": "ns" } },
          "crossings": { "items": "$.events[*]", "line": "@.line", "direction": { "path": "@.type", "in": ["INBOUND"], "out": ["OUTBOUND"] },
                         "trackId": "@.object", "time": { "path": "@.at", "unit": "ms" } },
          "occupancy": { "groups": "$.zones[*]", "items": "^.samples[*]", "zone": "^.name", "count": "@.n", "time": { "path": "@.s", "unit": "s" } },
          "intervals": { "items": "$.counts[*]", "line": "@.line", "in": "@.in", "from": { "path": "@.from", "unit": "rfc3339" }, "to": { "path": "$.header.sent", "unit": "rfc3339" } } }
        """), "full-v1");

    private const string FullPayload = """
        { "header": { "sent": "2026-10-02T18:05:00.250+04:00", "seq": 77 },
          "objects": [ { "uid": "a1", "kind": "person", "p": [12500, 8250], "t": 1790949900500000000 },
                       { "uid": 2, "kind": 1, "p": [13000, 8000], "t": 1790949900500000000 },
                       { "uid": "c3", "kind": "luggage", "p": [1, 1], "t": 1790949900500000000 } ],
          "events": [ { "line": "Entry A", "type": "INBOUND", "object": 17, "at": 1790949900600 },
                      { "line": "Entry A", "type": "INSIDE", "at": 1790949900600 } ],
          "zones": [ { "name": "Snake A", "samples": [ { "n": 40, "s": 1790949899 }, { "n": 41, "s": 1790949900.5 } ] } ],
          "counts": [ { "line": "Exit A", "in": 5, "from": "2026-10-02T14:00:00Z" } ] }
        """;

    private static MappedPush MapFull(string payload, int max = 2_000) =>
        DeclarativeMapper.Map(Parse(payload), Full, new DevicePose(100, 100, 45), max, Received);

    [Fact]
    public void Map_Should_ReadEverySection()
    {
        var mapped = MapFull(FullPayload);

        mapped.DeviceSentUtc.Should().Be(new DateTime(2026, 10, 2, 14, 5, 0, 250, DateTimeKind.Utc));
        mapped.PackageIds.Should().Equal(77);
        var tracks = mapped.Tracks.Select(t => (t.TrackId, Math.Round(t.X, 9), Math.Round(t.Y, 9), t.TimeUtc));
        tracks.Should().Equal(("a1", 12.5, 8.25, Received.AddMilliseconds(-500)), ("2", 13.0, 8.0, Received.AddMilliseconds(-500)));
        mapped.Crossings.Should().Equal(new LineCrossing("Entry A", CrossingDirection.In, "17", Received.AddMilliseconds(-400)));
        mapped.Occupancy.Should().Equal(new ZoneOccupancy("Snake A", 40, Received.AddSeconds(-2)), new ZoneOccupancy("Snake A", 41, Received.AddMilliseconds(-500)));
        mapped.Intervals.Should().Equal(new IntervalCount("Exit A", 5, 0, new DateTime(2026, 10, 2, 14, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 2, 14, 5, 0, 250, DateTimeKind.Utc)));
        mapped.Ignored.Should().Be(2, "the luggage and the INSIDE event are left out on purpose");
        mapped.Notes.Should().HaveCount(2);
        mapped.ReceiptTimed.Should().Be(PushKinds.None);
    }

    [Theory]
    [InlineData("\"uid\": \"a1\"", "\"uid\": 1.5", "tracks[0].trackId is not a string or an integer (@.uid).")]
    [InlineData("\"uid\": \"a1\", ", "", "tracks[0].trackId is missing (@.uid).")]
    [InlineData("\"p\": [12500, 8250]", "\"p\": [\"12500\", 8250]", "tracks[0].x is not a finite number (@.p[0]).")]
    [InlineData("\"p\": [12500, 8250]", "\"p\": [12500]", "tracks[0].y is missing (@.p[1]).")]
    [InlineData("\"t\": 1790949900500000000 },\n", "\"t\": \"2026-10-02T14:05:00Z\" },\n", "tracks[0].time is not a number.")]
    [InlineData("\"n\": 40", "\"n\": 40.5", "occupancy[0].count is not an integer (@.n).")]
    [InlineData("\"n\": 40", "\"n\": 99999999999", "occupancy[0].count is not an integer (@.n).")]
    [InlineData("\"s\": 1790949899", "\"s\": 99999999999999", "occupancy[0].time is out of range.")]
    [InlineData("\"s\": 1790949899", "\"s\": -5", "occupancy[0].time is out of range.")]
    [InlineData("\"line\": \"Exit A\"", "\"line\": 5", "intervals[0].line is not a string (@.line).")]
    [InlineData("\"from\": \"2026-10-02T14:00:00Z\"", "\"from\": \"2026-10-02T14:00:00\"", "intervals[0].from is not an RFC 3339 time with an offset.")]
    [InlineData("\"seq\": 77", "\"seq\": \"77\"", "package ($.header.seq) is not an integer.")]
    [InlineData("\"sent\": \"2026-10-02T18:05:00.250+04:00\"", "\"sent\": 5", "sentTime is not an RFC 3339 time.")]
    public void Map_Should_RefuseTheMessage_When_AValueIsMissingOrMistyped(string from, string to, string message)
    {
        var payload = FullPayload.Replace(from, to, StringComparison.Ordinal);
        payload.Should().NotBe(FullPayload);

        var act = () => MapFull(payload);

        act.Should().Throw<PushFormatException>().Which.Message.Should().Be(message);
    }

    [Fact]
    public void Map_Should_NeverEchoThePayload()
    {
        var hostile = FullPayload.Replace("\"uid\": \"a1\"", "\"uid\": {\"<script>alert(1)</script>\": 1}", StringComparison.Ordinal);

        var act = () => MapFull(hostile);

        act.Should().Throw<PushFormatException>().Which.Message.Should().NotContain("script").And.NotContain("alert");
    }

    /// <summary>A mapping that reads each sample's zone name from its group (^), and each crossing's line from the payload ($).</summary>
    private static readonly DeclarativeMapping Shared = DeclarativeMapping.Parse(Encoding.UTF8.GetBytes("""
        { "name": "shared-v1", "title": "Names shared by the items",
          "occupancy": { "groups": "$.zones[*]", "items": "^.samples[*]", "zone": "^.name", "count": "@.n", "time": { "path": "@.s", "unit": "s" } },
          "crossings": { "items": "$.events[*]", "line": "$.line", "direction": { "path": "@.d", "in": ["in"], "out": ["out"] },
                         "trackId": "@.id", "time": { "path": "@.s", "unit": "s" } } }
        """), "shared-v1");

    private static string SharedPayload(string zone, string line, string trackId = "t1", int samples = 1, int events = 1) =>
        "{ \"line\": " + JsonSerializer.Serialize(line) + ", \"zones\": [ { \"name\": " + JsonSerializer.Serialize(zone) + ", \"samples\": [" +
        string.Join(',', Enumerable.Repeat("{ \"n\": 4, \"s\": 1790949899 }", samples)) + "] } ], \"events\": [" +
        string.Join(',', Enumerable.Repeat("{ \"d\": \"in\", \"id\": " + JsonSerializer.Serialize(trackId) + ", \"s\": 1790949899 }", events)) + "] }";

    /// <summary>
    /// ARV-178 (CWE-400, CWE-120): a name or id is at most the canonical length, measured as decoded (an escaped character
    /// counts once), and a longer one refuses the message as a format error, before the canonical checks downstream.
    /// </summary>
    [Theory]
    [InlineData(200, 1, 64, null)]
    [InlineData(201, 1, 64, "occupancy[0].zone is longer than 200 characters (^.name).")]
    [InlineData(200, 201, 64, "crossings[0].line is longer than 200 characters ($.line).")]
    [InlineData(200, 1, 65, "crossings[0].trackId is longer than 64 characters (@.id).")]
    public void Map_Should_RefuseANameOrIdLongerThanTheCanonicalMaximum_When_AMappingReadsOne(int zone, int line, int trackId, string refusal)
    {
        var payload = SharedPayload(new string('z', zone), new string('l', line), new string('t', trackId));

        var act = () => DeclarativeMapper.Map(Parse(payload), Shared, new DevicePose(0, 0, 0), 2_000, Received);

        if (refusal is null)
            act.Should().NotThrow().Which.Occupancy.Should().ContainSingle().Which.ZoneName.Should().HaveLength(zone);
        else
            act.Should().Throw<PushFormatException>().Which.Message.Should().Be(refusal);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void Map_Should_CountAnEscapedCharacterOnce_When_ItMeasuresAName(int length, bool accepted)
    {
        // Every character written as a six-byte escape: the most a character can take as written.
        var escaped = string.Concat(Enumerable.Repeat("\\u00e9", length));
        var payload = SharedPayload("zone", "line").Replace("\"name\": \"zone\"", "\"name\": \"" + escaped + "\"", StringComparison.Ordinal);

        var act = () => DeclarativeMapper.Map(Parse(payload), Shared, new DevicePose(0, 0, 0), 2_000, Received);

        if (accepted)
            act.Should().NotThrow().Which.Occupancy.Single().ZoneName.Should().Be(new string('\u00e9', length));
        else
            act.Should().Throw<PushFormatException>().WithMessage("occupancy[0].zone is longer than 200 characters*");
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void Map_Should_CountASurrogatePairAsTwo_When_ItMeasuresAName(int pairs, bool accepted)
    {
        // A character outside the BMP is two UTF-16 code units, as CanonicalEventRules counts it; escaped, each pair takes 12 bytes.
        var escaped = string.Concat(Enumerable.Repeat("\\uD83D\\uDE00", pairs));
        var payload = SharedPayload("zone", "line").Replace("\"name\": \"zone\"", "\"name\": \"" + escaped + "\"", StringComparison.Ordinal);

        var act = () => DeclarativeMapper.Map(Parse(payload), Shared, new DevicePose(0, 0, 0), 2_000, Received);

        if (accepted)
            act.Should().NotThrow().Which.Occupancy.Single().ZoneName.Should().Be(string.Concat(Enumerable.Repeat("\uD83D\uDE00", pairs)));
        else
            act.Should().Throw<PushFormatException>().WithMessage("occupancy[0].zone is longer than 200 characters*");
    }

    /// <summary>
    /// ARV-178, the ARV-174 review's recommendation 1: a group-level (^) or payload-level ($) name is read for every item, so
    /// before the bound a 500 KB name under 2,000 items was copied 2,000 times (about 2 GB). Now the first item refuses the
    /// message, and it is never copied whole.
    /// </summary>
    [Theory]
    [InlineData("zone")]
    [InlineData("line")]
    public void Map_Should_NotCopyAHugeSharedNamePerItem_When_TheMappingReadsItFromTheGroupOrThePayload(string which)
    {
        var huge = new string('x', 500_000);
        // 2,000 items in the section that reads the huge name, one in the other (crossings are mapped before occupancy).
        var payload = which == "zone" ? SharedPayload(huge, "line", samples: 2_000) : SharedPayload("zone", huge, events: 2_000);
        var root = Parse(payload);
        string Refusal()
        {
            try
            {
                DeclarativeMapper.Map(root, Shared, new DevicePose(0, 0, 0), 2_000, Received);
                return null;
            }
            catch (PushFormatException e)
            {
                return e.Message;
            }
        }

        Refusal().Should().Contain(" is longer than 200 characters");
        // Measured on a second call, so that loading and compiling the code on the way are not counted.
        var before = GC.GetAllocatedBytesForCurrentThread();
        var refusal = Refusal();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        refusal.Should().Contain(" is longer than 200 characters");
        allocated.Should().BeLessThan(500_000, "the name (1 MB as a string) is measured as written and refused before any copy of it");
    }

    /// <summary>
    /// ARV-256: a filter and a direction compared against a value of the payload ($) or the group (^). Before, the value
    /// was copied for every item (a 250,000 character value under 8,000 items, about 4 GB of short-lived copies); now one
    /// longer than any value a mapping may name matches none and is never copied.
    /// </summary>
    private static readonly DeclarativeMapping SharedFilter = DeclarativeMapping.Parse(Encoding.UTF8.GetBytes("""
        { "name": "shared-filter-v1", "title": "A filter and a direction shared by the items",
          "crossings": { "items": "$.events[*]", "where": [{ "path": "$.kind", "equals": ["people"] }], "line": "@.line",
                         "direction": { "path": "$.d", "in": ["in"], "out": ["out"] }, "time": { "path": "@.s", "unit": "s" } } }
        """), "shared-filter-v1");

    /// <summary>The same, read from the group (^): the items of one batch share its kind and direction.</summary>
    private static readonly DeclarativeMapping GroupFilter = DeclarativeMapping.Parse(Encoding.UTF8.GetBytes("""
        { "name": "group-filter-v1", "title": "A filter and a direction shared by a group's items",
          "crossings": { "groups": "$.batches[*]", "items": "^.events[*]", "where": [{ "path": "^.kind", "equals": ["people"] }], "line": "@.line",
                         "direction": { "path": "^.d", "in": ["in"], "out": ["out"] }, "time": { "path": "@.s", "unit": "s" } } }
        """), "group-filter-v1");

    [Theory]
    [InlineData("filter", "$")]
    [InlineData("direction", "$")]
    [InlineData("filter", "^")]
    [InlineData("direction", "^")]
    public void Map_Should_NotCopyAHugeSharedFilterOrDirectionValue_When_EveryItemIsComparedWithIt(string which, string level)
    {
        // Against the same 8,000 items with a short value that matches nothing either: the walk's own allocations (the item
        // list, a filter's closure per item) are the same in both, so the difference is what the huge value cost.
        var huge = Allocated(which, level, new string('x', 250_000), out var hugeIgnored);
        var plain = Allocated(which, level, "other", out var plainIgnored);

        hugeIgnored.Should().Be(8_000, "a value longer than any the mapping names matches no filter and no direction");
        plainIgnored.Should().Be(8_000);
        (huge - plain).Should().BeLessThan(250_000, "the value (500 KB as a string) is measured as written and never copied, not even once");
    }

    /// <summary>
    /// The bytes one mapping of 8,000 items allocates with <paramref name="value"/> as the filter or direction value they
    /// share, read from the payload ($) or from their one group (^).
    /// </summary>
    private static long Allocated(string which, string level, string value, out int ignored)
    {
        var (kind, direction) = which == "filter" ? (value, "in") : ("people", value);
        var shared = "\"kind\": " + JsonSerializer.Serialize(kind) + ", \"d\": " + JsonSerializer.Serialize(direction) + ", \"events\": [" +
            string.Join(',', Enumerable.Repeat("{ \"line\": \"Entry A\", \"s\": 1790949899 }", 8_000)) + "]";
        var root = Parse(level == "$" ? "{ " + shared + " }" : "{ \"batches\": [{ " + shared + " }] }");
        var mapping = level == "$" ? SharedFilter : GroupFilter;
        MappedPush Map() => DeclarativeMapper.Map(root, mapping, new DevicePose(0, 0, 0), 2_000, Received);

        // Measured on a second call, so that loading and compiling the code on the way are not counted.
        Map();
        var before = GC.GetAllocatedBytesForCurrentThread();
        ignored = Map().Ignored;
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Theory]
    [InlineData("\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"", 64)]
    [InlineData("\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"", null)]
    [InlineData("1234567890123456789012345678901234567890123456789012345678901234", 64)]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345", null)]
    [InlineData("true", 4)]
    [InlineData("{}", null)]
    public void Scalar_Should_MatchNone_When_TheValueIsLongerThanAnyAMappingMayName(string json, int? length)
    {
        var value = DeclarativeMapper.Scalar(Parse(json));

        if (length is null)
            value.Should().BeNull();
        else
            value.Should().HaveLength(length.Value);
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void Scalar_Should_CountAnEscapedCharacterOnce_When_ItMeasuresAValue(int length, bool kept)
    {
        // Six bytes a character as written, the most there is: 64 of them are read, 65 are refused without a copy.
        var value = DeclarativeMapper.Scalar(Parse("\"" + string.Concat(Enumerable.Repeat("\\u00e9", length)) + "\""));

        if (kept)
            value.Should().Be(new string('\u00e9', length));
        else
            value.Should().BeNull();
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void Scalar_Should_CountASurrogatePairAsTwo_When_ItMeasuresAValue(int pairs, bool kept)
    {
        // A pair is two UTF-16 code units and 12 bytes escaped: 32 pairs are 64 units in 384 bytes, read; 33 are past both.
        var value = DeclarativeMapper.Scalar(Parse("\"" + string.Concat(Enumerable.Repeat("\\uD83D\\uDE00", pairs)) + "\""));

        if (kept)
            value.Should().Be(string.Concat(Enumerable.Repeat("\U0001F600", pairs)));
        else
            value.Should().BeNull();
    }

    /// <summary>
    /// ARV-256: values read from the group (^) are decoded once per group and values read from the payload ($) once per
    /// message, not once per item: three groups of five samples read the zone and the count from their group and the time
    /// and a filter's value from the payload, which is 3 x 2 + 2 decodes, where every item decoding them would be 60.
    /// </summary>
    [Fact]
    public void Map_Should_DecodeAGroupValueOncePerGroupAndAPayloadValueOncePerMessage_When_ItemsReadThem()
    {
        var mapping = DeclarativeMapping.Parse(Encoding.UTF8.GetBytes("""
            { "name": "shared-decode-v1", "title": "Values shared by the items",
              "occupancy": { "groups": "$.zones[*]", "items": "^.samples[*]", "where": [{ "path": "$.site", "equals": ["T1"] }],
                             "zone": "^.name", "count": "^.n", "time": { "path": "$.s", "unit": "s" } } }
            """), "shared-decode-v1");
        var group = (string name, int n) => "{ \"name\": \"" + name + "\", \"n\": " + n + ", \"samples\": [{}, {}, {}, {}, {}] }";
        var payload = "{ \"site\": \"T1\", \"s\": 1790949899, \"zones\": [" + group("Snake A", 4) + ", " + group("Snake B", 7) + ", " + group("Snake C", 9) + "] }";
        var root = Parse(payload);

        DeclarativeMapper.SharedDecodes = 0;
        var mapped = DeclarativeMapper.Map(root, mapping, new DevicePose(0, 0, 0), 2_000, Received);
        var decodes = DeclarativeMapper.SharedDecodes;

        mapped.Occupancy.Should().HaveCount(15);
        mapped.Occupancy.Select(o => (o.ZoneName, o.Count)).Distinct().Should().Equal(("Snake A", 4), ("Snake B", 7), ("Snake C", 9));
        mapped.Occupancy.Select(o => o.TimeUtc).Distinct().Should().ContainSingle();
        decodes.Should().Be(8, "the zone and the count once for each of the three groups, the time and the filter's value once for the message");
        mapped.Occupancy.Chunk(5).Should().OnlyContain(samples => samples.All(o => ReferenceEquals(o.ZoneName, samples[0].ZoneName)),
            "the five samples of a group share the one name decoded for it");
    }

    [Fact]
    public void Map_Should_HoldTheEventAndWorkLimits()
    {
        var many = "{ \"objects\": [" + string.Join(',', Enumerable.Repeat("{ \"uid\": \"a\", \"kind\": \"person\", \"p\": [1, 1], \"t\": 1790949900500000000 }", 11)) + "] }";
        ((Action)(() => MapFull(many, max: 10))).Should().Throw<PushFormatException>().WithMessage("At most 10 events per message.");

        var filtered = "{ \"objects\": [" + string.Join(',', Enumerable.Repeat("{ \"kind\": \"luggage\" }", 41)) + "] }";
        ((Action)(() => MapFull(filtered, max: 10))).Should().Throw<PushFormatException>().WithMessage("At most 40 items per message.",
            "items left out by a filter still count toward the work limit");
        var fits = "{ \"objects\": [" + string.Join(',', Enumerable.Repeat("{ \"kind\": \"luggage\" }", 40)) + "] }";
        MapFull(fits, max: 10).Ignored.Should().Be(40);

        var groups = "{ \"zones\": [" + string.Join(',', Enumerable.Repeat("{ \"name\": \"Snake A\", \"samples\": [] }", 41)) + "] }";
        ((Action)(() => MapFull(groups, max: 10))).Should().Throw<PushFormatException>().WithMessage("At most 40 occupancy groups per message.");
    }

    [Fact]
    public void Map_Should_AcceptAnEmptyOrUnrelatedPayload()
    {
        var mapped = MapFull("""{ "heartbeat": true }""");

        mapped.EventCount.Should().Be(0);
        mapped.DeviceSentUtc.Should().BeNull();
        ((Action)(() => MapFull("42"))).Should().Throw<PushFormatException>();
    }
}
