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

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void Map_Should_ProduceTheExpectedCanonicalEvents_When_GivenASample(string mappingName, string sample)
    {
        var expected = Parse(File.ReadAllText(Path.Combine(Samples, mappingName, "expected", sample + ".json")));
        var pose = expected.GetProperty("pose");
        var mapping = DeclarativeMappingCatalog.Embedded.Find(mappingName);

        var mapped = DeclarativeMapper.Map(Parse(File.ReadAllText(Path.Combine(Samples, mappingName, sample + ".json"))), mapping,
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
