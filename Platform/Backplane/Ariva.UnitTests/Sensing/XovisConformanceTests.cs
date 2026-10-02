using System.Text;
using System.Text.Json;
using Ariva.Core.Sensing;
using Ariva.Infra.Sensing;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Sensing;

/// <summary>
/// ARV-023 conformance: every Xovis sample in Sensing/Samples maps to exactly its expected canonical events; malformed,
/// legacy, oversized and hostile payloads are refused with a message that never echoes the payload; the canonical
/// dialect is strict.
/// </summary>
public sealed class XovisConformanceTests
{
    private static string Samples => RepositoryPaths.Resolve("Platform/Backplane/Ariva.UnitTests/Sensing/Samples");

    public static TheoryData<string> SampleNames()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(Samples, "expected"), "*.json").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileNameWithoutExtension(file));
        return data;
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static DateTime Utc(JsonElement e) => DateTimeOffset.Parse(e.GetString()!, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime;

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void Map_Should_ProduceTheExpectedCanonicalEvents_When_GivenAXovisSample(string name)
    {
        var expected = Parse(File.ReadAllText(Path.Combine(Samples, "expected", name + ".json")));
        var pose = expected.GetProperty("pose");

        var mapped = XovisPushMapper.Map(Parse(File.ReadAllText(Path.Combine(Samples, name + ".json"))),
            new DevicePose(pose[0].GetDouble(), pose[1].GetDouble(), pose[2].GetDouble()), 2_000);

        if (expected.GetProperty("sentUtc").ValueKind == JsonValueKind.Null)
            mapped.DeviceSentUtc.Should().BeNull();
        else
            mapped.DeviceSentUtc.Should().Be(Utc(expected.GetProperty("sentUtc")));
        mapped.PackageIds.Should().Equal(expected.GetProperty("packages").EnumerateArray().Select(p => p.GetInt64()));
        mapped.Ignored.Should().Be(expected.GetProperty("ignored").GetInt32());
        mapped.ConnectionTest.Should().Be(expected.TryGetProperty("connectionTest", out var test) && test.GetBoolean());
        mapped.Intervals.Should().Equal(expected.GetProperty("intervals").EnumerateArray().Select(i =>
            new IntervalCount(i.GetProperty("line").GetString(), i.GetProperty("in").GetInt32(), i.GetProperty("out").GetInt32(), Utc(i.GetProperty("fromUtc")), Utc(i.GetProperty("toUtc")))));
        mapped.Occupancy.Should().Equal(expected.GetProperty("occupancy").EnumerateArray().Select(o =>
            new ZoneOccupancy(o.GetProperty("zone").GetString(), o.GetProperty("count").GetInt32(), Utc(o.GetProperty("timeUtc")))));
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

        mapped.Intervals.Concat<CanonicalEvent>(mapped.Occupancy).Concat(mapped.Tracks)
            .Where(e => CanonicalEventRules.Validate(e).Count > 0).Should().BeEmpty("every mapped event is within the canonical bounds");
    }

    [Fact]
    public void Map_Should_RefuseTheLegacyFormat()
    {
        var act = () => XovisPushMapper.Map(Parse(File.ReadAllText(Path.Combine(Samples, "xovis-legacy-event.json"))), new DevicePose(0, 0, 0), 2_000);

        act.Should().Throw<PushFormatException>().WithMessage("*Unsupported Xovis envelope*");
    }

    [Theory]
    [InlineData("42", "JSON object")]
    [InlineData("[]", "1 to 100")]
    [InlineData("{\"status\":{}}", "Unsupported Xovis envelope")]
    [InlineData("{\"logics_data\":[]}", "not an object")]
    [InlineData("{\"logics_data\":{}}", "logics")]
    [InlineData("{\"logics_data\":{\"sensor_info\":{\"time\":\"2026-10-02T18:05:00\"},\"logics\":[]}}", "with an offset")]
    [InlineData("{\"logics_data\":{\"logics\":[{\"name\":\"L\",\"records\":[{\"from\":\"x\",\"to\":\"y\",\"counts\":[]}]}]}}", "RFC 3339")]
    [InlineData("{\"logics_data\":{\"logics\":[{\"name\":\"L\",\"records\":[{\"from\":-5,\"to\":1,\"counts\":[]}]}]}}", "not a time")]
    [InlineData("{\"live_data\":{\"frames\":[{\"tracked_objects\":[]}]}}", "time is missing")]
    [InlineData("{\"live_data\":{\"frames\":[{\"time\":1790949900000,\"tracked_objects\":[{\"type\":\"PERSON\",\"position\":\"0,0\"}]}]}}", "position")]
    [InlineData("{\"live_data\":{\"frames\":[{\"time\":1790949900000,\"tracked_objects\":[{\"type\":\"PERSON\",\"position\":[\"a\",1]}]}]}}", "finite number")]
    public void Map_Should_RefuseMalformedPayloads(string json, string message)
    {
        var act = () => XovisPushMapper.Map(Parse(json), new DevicePose(0, 0, 0), 2_000);

        act.Should().Throw<PushFormatException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void Map_Should_RefuseMoreEventsThanTheLimit()
    {
        var objects = string.Join(',', Enumerable.Range(0, 11).Select(i => $"{{\"track_id\":{i},\"type\":\"PERSON\",\"position\":[0,0,1.7]}}"));
        var json = $"{{\"live_data\":{{\"frames\":[{{\"time\":1790949900000,\"tracked_objects\":[{objects}]}}]}}}}";

        var act = () => XovisPushMapper.Map(Parse(json), new DevicePose(0, 0, 0), 10);

        act.Should().Throw<PushFormatException>().WithMessage("*At most 10 events*");
    }

    [Fact]
    public void Map_Should_NotEchoThePayload_When_ItRefusesIt()
    {
        const string marker = "<script>steal()</script>";
        var json = $"{{\"logics_data\":{{\"sensor_info\":{{\"time\":\"{marker}\"}},\"logics\":[]}}}}";

        var act = () => XovisPushMapper.Map(Parse(json), new DevicePose(0, 0, 0), 10);

        act.Should().Throw<PushFormatException>().Which.Message.Should().NotContain(marker);
    }

    [Fact]
    public void Map_Should_QuoteVendorNamesSafely_When_ItReportsThem()
    {
        var json = "{\"logics_data\":{\"logics\":[{\"name\":\"<img src=x onerror=alert(1)>\\r\\nINJECTED\",\"records\":[{\"from\":1790949600000,\"to\":1790949900000,\"counts\":[{\"name\":\"dwell\",\"value\":1}]}]}]}}";

        var mapped = XovisPushMapper.Map(Parse(json), new DevicePose(0, 0, 0), 10);

        mapped.Notes.Single().Should().NotContain("<").And.NotContain("\r").And.NotContain("\n").And.Contain("'?img src?x onerror?alert?1????INJECTED'");
    }

    [Fact]
    public void Canonical_Should_NotEchoAnUnknownMember()
    {
        var json = "{\"tracks\":[{\"trackId\":\"7\",\"x\":1,\"y\":1,\"timeUtc\":\"2026-10-02T14:05:00Z\",\"<script>alert(1)</script>\":1}]}";

        var act = () => CanonicalPushMapper.Map(Parse(json), 2_000);

        act.Should().Throw<PushFormatException>().Which.Message.Should().NotContain("script").And.Contain("$.tracks[0]");
    }

    [Fact]
    public void Canonical_Should_MapAStrictPush()
    {
        var json = """
            { "sentUtc": "2026-10-02T14:05:00.250Z", "packageId": 4711,
              "tracks": [{ "trackId": "7", "x": 12.5, "y": 8.25, "heightMetres": 1.72, "timeUtc": "2026-10-02T14:04:59.800Z" }],
              "crossings": [{ "lineName": "Entry A", "direction": "In", "trackId": "7", "timeUtc": "2026-10-02T14:04:59.900Z" }],
              "occupancy": [{ "zoneName": "Snake A", "count": 41, "timeUtc": "2026-10-02T14:05:00Z" }],
              "intervals": [{ "lineName": "Entry A", "in": 30, "out": 2, "fromUtc": "2026-10-02T14:00:00Z", "toUtc": "2026-10-02T14:05:00+00:00" }],
              "status": { "online": true, "temperatureCelsius": 41.5, "frameRate": 12.5, "clockOffsetMilliseconds": -18, "timeUtc": "2026-10-02T14:05:00Z" } }
            """;

        var mapped = CanonicalPushMapper.Map(Parse(json), 2_000);

        mapped.PackageIds.Should().Equal(4711);
        mapped.DeviceSentUtc.Should().Be(new DateTime(2026, 10, 2, 14, 5, 0, 250, DateTimeKind.Utc));
        mapped.Tracks.Single().Should().Be(new TrackPosition("7", 12.5, 8.25, 1.72, new DateTime(2026, 10, 2, 14, 4, 59, 800, DateTimeKind.Utc)));
        mapped.Crossings.Single().Direction.Should().Be(Ariva.Core.Domain.Enums.CrossingDirection.In);
        mapped.Intervals.Single().Should().Be(new IntervalCount("Entry A", 30, 2, new DateTime(2026, 10, 2, 14, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 2, 14, 5, 0, DateTimeKind.Utc)));
        mapped.Status!.ClockOffsetMilliseconds.Should().Be(-18);
        mapped.EventCount.Should().Be(5);
    }

    [Theory]
    [InlineData("{\"tracks\":[{\"trackId\":\"7\",\"x\":1,\"y\":1,\"timeUtc\":\"2026-10-02T14:05:00Z\",\"extra\":1}]}", "tracks")]
    [InlineData("{\"crossings\":[{\"lineName\":\"L\",\"direction\":0,\"timeUtc\":\"2026-10-02T14:05:00Z\"}]}", "direction")]
    [InlineData("{\"tracks\":[{\"trackId\":\"7\",\"x\":\"1\",\"y\":1,\"timeUtc\":\"2026-10-02T14:05:00Z\"}]}", "x")]
    [InlineData("{\"tracks\":[{\"trackId\":\"7\",\"x\":1,\"y\":1,\"timeUtc\":\"2026-10-02T18:05:00+04:00\"}]}", "must be UTC")]
    [InlineData("{\"tracks\":[{\"trackId\":\"7\",\"x\":1,\"y\":1}]}", "timeUtc is missing")]
    [InlineData("{\"occupancy\":[{\"zoneName\":\"Z\",\"timeUtc\":\"2026-10-02T14:05:00Z\"}]}", "count is missing")]
    [InlineData("[]", "JSON object")]
    public void Canonical_Should_RefuseAnythingButTheCanonicalShape(string json, string message)
    {
        var act = () => CanonicalPushMapper.Map(Parse(json), 2_000);

        act.Should().Throw<PushFormatException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void Canonical_Should_RefuseMoreEventsThanTheLimit()
    {
        var tracks = string.Join(',', Enumerable.Range(0, 3).Select(i => $"{{\"trackId\":\"{i}\",\"x\":1,\"y\":1,\"timeUtc\":\"2026-10-02T14:05:00Z\"}}"));

        var act = () => CanonicalPushMapper.Map(Parse($"{{\"tracks\":[{tracks}]}}"), 2);

        act.Should().Throw<PushFormatException>().WithMessage("*At most 2 events*");
    }

    [Fact]
    public void Samples_Should_StayFarBelowTheBodyLimit() =>
        Directory.GetFiles(Samples, "*.json").Should().OnlyContain(f => Encoding.UTF8.GetByteCount(File.ReadAllText(f)) < IngestSettings.MaxBodyBytes);
}
