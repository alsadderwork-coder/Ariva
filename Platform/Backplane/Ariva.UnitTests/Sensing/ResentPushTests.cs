using System.Text;
using Ariva.Core.Domain.Contracts;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using Ariva.Core.Services.Sensing;
using Ariva.Infra.Sensing;
using Ariva.Infra.Sensing.Declarative;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.UnitTests.Sensing;

/// <summary>
/// ARV-174: a resent push gets the batch ids of the push it repeats, so the queue engine (and the archive and the inbox)
/// apply it once; other data gets new ids. A kind whose events carry their own times is identified by the device, the
/// kind, the vendor package ids and its events, so a retry that stamps <c>sentUtc</c> (or Xovis <c>sensor_info.time</c>)
/// again, or lays the body out otherwise, is the same batch. A kind stamped with Ariva's receipt time is identified by its
/// body and the package ids when the mapping names a package, and otherwise by its body and receipt time, so without a
/// package a resend is new data (the residual the mapping guidance states and the conformance kit warns about).
/// </summary>
public sealed class ResentPushTests
{
    private static readonly DateTime Received = new(2026, 10, 2, 14, 5, 1, DateTimeKind.Utc);

    /// <summary>Crossings without a time of their own, stamped at receipt, with a package (a sequence number) or not.</summary>
    private static DeclarativeMapping ReceivedCrossings(string name, bool package) => DeclarativeMapping.Parse(Encoding.UTF8.GetBytes($$"""
        { "name": "{{name}}", "title": "Receipt-timed crossings (ARV-174 test)", "source": "https://example.com/spec",
          {{(package ? "\"package\": \"$.seq\"," : string.Empty)}}
          "crossings": { "items": "$.events[*]", "line": "@.line", "direction": { "path": "@.dir", "in": ["in"], "out": ["out"] }, "time": { "received": true } } }
        """), name);

    private static readonly DeclarativeMappingCatalog Catalog = new([ReceivedCrossings("seq-crossings-v1", true), ReceivedCrossings("bare-crossings-v1", false)]);

    private sealed class Gateway : ISvcDeviceGateway
    {
        public Task<DeviceCredentialRecord> FindByPrefixAsync(string prefix, CancellationToken ct = default) => Task.FromResult<DeviceCredentialRecord>(null);

        public Task<Fluentx.Result<DeviceZoneViewModel>> PublishedZoneAsync(string siteCode, string queueZoneName, CancellationToken ct = default) =>
            Task.FromResult(new Fluentx.Result<DeviceZoneViewModel>(new DeviceZoneViewModel(siteCode, queueZoneName, 12, new string('b', 64),
                [new(Guid.NewGuid(), "Snake A", "Queue", Guid.NewGuid(), null, null, "10 10,34 10,34 22,10 22", 288), new(Guid.NewGuid(), "Overflow A", "Overflow", Guid.NewGuid(), null, null, "10 4,34 4,34 10,10 10", 144)],
                [new(Guid.NewGuid(), "Entry A", "Entry", null, Guid.NewGuid(), 10, 12, 10, 16, 4), new(Guid.NewGuid(), "Exit A", "Exit", null, Guid.NewGuid(), 30, 22, 34, 22, 4)])));
    }

    private sealed class Sink : ISensingSink
    {
        public List<IEvent> Published { get; } = [];

        public Task PublishAsync(IReadOnlyList<IEvent> events, CancellationToken ct = default)
        {
            Published.AddRange(events);
            return Task.CompletedTask;
        }
    }

    private static DeviceCredentialRecord Device(string dialect, string mapping = null) =>
        new(Guid.Parse("0199a000-0000-7000-8000-0000000d0031"), "S-31", "DMO", "Snake A", "Online", new string('a', 64), [], null, dialect, 20, 16, 0, mapping);

    /// <summary>
    /// A canonical push of crossings (two in, one out; one in when <paramref name="single"/>) and, unless single, an occupancy
    /// reading: sent at <paramref name="sent"/> (no <c>sentUtc</c> when null), with a package or not, with track ids or
    /// anonymous; <paramref name="reordered"/> writes the same members and events in another order and layout.
    /// </summary>
    private static string Canonical(string sent, long? package, bool tracked = false, bool single = false, bool reordered = false)
    {
        string Crossing(string line, string direction, string time, int track) =>
            $$"""{ "lineName": "{{line}}", "direction": "{{direction}}"{{(tracked ? $", \"trackId\": \"{track}\"" : string.Empty)}}, "timeUtc": "{{time}}" }""";
        List<string> crossings = single
            ? [Crossing("Entry A", "In", "2026-10-02T14:04:10Z", 7)]
            : [Crossing("Entry A", "In", "2026-10-02T14:04:10Z", 7), Crossing("Entry A", "In", "2026-10-02T14:04:20Z", 8), Crossing("Exit A", "Out", "2026-10-02T14:04:40Z", 9)];
        var members = new List<string>();
        if (sent is not null)
            members.Add($"\"sentUtc\": \"{sent}\"");
        if (package is { } id)
            members.Add($"\"packageId\": {id}");
        members.Add($"\"crossings\": [{string.Join(",\n  ", reordered ? Enumerable.Reverse(crossings) : crossings)}]");
        if (!single)
            members.Add("\"occupancy\": [{ \"zoneName\": \"Snake A\", \"count\": 41, \"timeUtc\": \"2026-10-02T14:05:00Z\" }]");
        return reordered ? "{" + string.Join(",", Enumerable.Reverse(members)) + "}" : "{ " + string.Join(",\n  ", members) + " }";
    }

    /// <summary>A message of receipt-timed crossings (two in, one out), with a sequence number or not.</summary>
    private static string Declarative(long? seq = 7) => $$"""
        { {{(seq is null ? "" : $"\"seq\": {seq}, ")}}"events": [{ "line": "Entry A", "dir": "in" }, { "line": "Entry A", "dir": "in" }, { "line": "Exit A", "dir": "out" }] }
        """;

    private static string Xovis(string sent) =>
        File.ReadAllText(RepositoryPaths.Resolve("Platform/Backplane/Ariva.UnitTests/Sensing/Samples/xovis-logics-fw5.json"))
            .Replace("\"time\":\"2026-10-02T18:05:00.741+04:00\"", $"\"time\":\"{sent}\"", StringComparison.Ordinal);

    private static async Task<List<SensingBatch>> IngestAsync(DeviceCredentialRecord device, DeviceDialect dialect, string body, DateTime received, Sink sink, DeviceClockStore clocks)
    {
        var cache = new FusionCache(new FusionCacheOptions(), new MemoryCache(new MemoryCacheOptions()));
        var ingest = new SensingIngest(new Gateway(), sink, clocks, cache, Options.Create(new IngestSettings()), Catalog);
        var before = sink.Published.Count;
        var result = await ingest.IngestAsync(device, dialect, Encoding.UTF8.GetBytes(body), received, CancellationToken.None);
        result.HasErrors.Should().BeFalse(string.Join("; ", result.ErrorMessages ?? []));
        result.Data.Rejected.Should().Be(0, string.Join("; ", result.Data.Problems));
        return [.. sink.Published.Skip(before).OfType<SensingBatch>()];
    }

    public static TheoryData<string, string, string, string, double, bool, int> Pairs => new()
    {
        // Each case: the dialect (or the mapping), the first body, the second body, how long after the first the second
        // arrives, whether the second's batch of the kind that counts the entries (crossings; intervals for Xovis) has the
        // id of the first's, and the entries the queue engine counts from both. A body names a builder: c (canonical), d
        // (declarative), x (Xovis).
        { "canonical: the retry stamps sentUtc again", "Canonical", "c", "c sent=2026-10-02T14:05:21.300Z", 20, true, 2 },
        { "canonical: the retry also lays the body out otherwise", "Canonical", "c", "c sent=2026-10-02T14:05:21.300Z reordered", 20, true, 2 },
        { "canonical: tracked crossings without a package, the retry stamps sentUtc again", "Canonical", "c package=none tracked", "c package=none tracked sent=2026-10-02T14:05:21.300Z", 20, true, 2 },
        { "canonical: anonymous crossings without a package, the retry keeps its sentUtc", "Canonical", "c package=none", "c package=none", 20, true, 2 },
        // R1: anonymous crossings of a push without a package are told apart by the send time, so a retry that stamps it
        // again is counted again (as before ARV-174), and two people crossing one line in the same second, sent as two
        // messages, both count.
        { "canonical: anonymous crossings without a package, the retry stamps sentUtc again (the residual)", "Canonical", "c package=none", "c package=none sent=2026-10-02T14:05:21.300Z", 20, false, 4 },
        { "canonical: two people in one second, one message each, no package", "Canonical", "c package=none single", "c package=none single sent=2026-10-02T14:05:01.900Z", 1, false, 2 },
        { "canonical: anonymous crossings without a package or sentUtc, a resend is new data (the receipt time)", "Canonical", "c package=none sent=none", "c package=none sent=none", 20, false, 4 },
        { "canonical: two packages with the same events both count", "Canonical", "c", "c package=4712", 20, false, 4 },
        { "canonical: a crossing at another time is new data", "Canonical", "c", "c sent=2026-10-02T14:05:21.300Z shifted", 20, false, 4 },
        { "receipt-timed crossings with a package: the resend counts once", "seq-crossings-v1", "d", "d", 20, true, 2 },
        { "receipt-timed crossings: two packages both count", "seq-crossings-v1", "d", "d seq=8", 20, false, 4 },
        { "receipt-timed crossings without a package: a resend is new data (the residual)", "bare-crossings-v1", "d seq=none", "d seq=none", 20, false, 4 },
        { "Xovis: the retry stamps sensor_info.time again", "Xovis", "x sent=2026-10-02T18:05:00.741+04:00", "x sent=2026-10-02T18:05:21.300+04:00", 20, true, 31 }
    };

    private static string Body(string spec)
    {
        var parts = spec.Split(' ');
        string Arg(string name) => parts.Skip(1).Select(p => p.Split('=')).Where(p => p[0] == name).Select(p => p.Length > 1 ? p[1] : string.Empty).FirstOrDefault();
        long? Package(string name, long fallback) => Arg(name) switch { null => fallback, "none" => null, var v => long.Parse(v, System.Globalization.CultureInfo.InvariantCulture) };
        var sent = Arg("sent") switch { null => "2026-10-02T14:05:00.900Z", "none" => null, var v => v };
        return parts[0] switch
        {
            "c" => Canonical(sent, Package("package", 4711), parts.Contains("tracked"), parts.Contains("single"), parts.Contains("reordered")) is var c && parts.Contains("shifted")
                ? c.Replace("14:04:10Z", "14:04:11Z", StringComparison.Ordinal)
                : c,
            "d" => Declarative(Package("seq", 7)),
            _ => Xovis(Arg("sent"))
        };
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public async Task Ingest_Should_GiveAResendTheIdsOfThePushItRepeats_And_TheZoneCountItOnce(string because, string dialectOrMapping, string first, string second, double after,
        bool same, int entries)
    {
        var (dialect, device) = dialectOrMapping switch
        {
            "Canonical" => (DeviceDialect.Canonical, Device("Canonical")),
            "Xovis" => (DeviceDialect.Xovis, Device("Xovis")),
            var mapping => (DeviceDialect.Declarative, Device("Declarative", mapping))
        };
        var sink = new Sink();
        var clocks = new DeviceClockStore();
        // A Xovis sample's events are those of 18:00 to 18:05 at UTC+4, so it is received at 14:05:01 UTC like the others.
        var a = await IngestAsync(device, dialect, Body(first), Received, sink, clocks);
        var b = await IngestAsync(device, dialect, Body(second), Received.AddSeconds(after), sink, clocks);

        a.Should().NotBeEmpty();
        var counting = dialect == DeviceDialect.Xovis ? typeof(IntervalCountBatch) : typeof(VendorLineCrossingBatch);
        var firstIds = a.ToDictionary(x => x.GetType(), x => x.Id);
        (b.Single(x => x.GetType() == counting).Id == firstIds[counting]).Should().Be(same, because);
        var repeated = b.Count(x => firstIds.GetValueOrDefault(x.GetType()) == x.Id);

        // The same pushes through a queue zone: a batch with an id the device sent before is applied once (ARV-168).
        var zone = new ZoneProcessor("DMO/Snake A", new QueueZoneGeometry("Snake A", new HashSet<string> { "Entry A" }, new HashSet<string> { "Exit A" }, new HashSet<string>(),
            new HashSet<string> { "Overflow A" }), 12);
        foreach (var batch in a.Concat(b))
            zone.Offer(batch, DateTime.MaxValue);
        zone.Tick(Received.AddHours(3));
        zone.Drain().Lines.Where(l => l.LineName == "Entry A").Sum(l => l.In).Should().Be(entries, because);
        zone.Counters.RepeatedBatches.Should().Be(repeated, "{0}: every batch with an id seen before is dropped, and only those", because);
    }

    [Fact]
    public async Task Ingest_Should_KeepAKindsIdWhateverTheOtherKindsCarry()
    {
        // The id of a kind hashes that kind's events only: a push whose occupancy changed keeps its crossings' id.
        var sink = new Sink();
        var clocks = new DeviceClockStore();
        var a = await IngestAsync(Device("Canonical"), DeviceDialect.Canonical, Body("c"), Received, sink, clocks);
        var b = await IngestAsync(Device("Canonical"), DeviceDialect.Canonical, Body("c").Replace("\"count\": 41", "\"count\": 42", StringComparison.Ordinal), Received.AddSeconds(20), sink, clocks);

        b.OfType<VendorLineCrossingBatch>().Single().Id.Should().Be(a.OfType<VendorLineCrossingBatch>().Single().Id);
        b.OfType<ZoneOccupancyBatch>().Single().Id.Should().NotBe(a.OfType<ZoneOccupancyBatch>().Single().Id);
        a.Select(x => x.Id).Should().OnlyHaveUniqueItems("every kind of one push has an id of its own");
    }

    [Fact]
    public async Task Ingest_Should_GiveAnotherDevicesIdenticalPushOtherIds()
    {
        // CWE-501: the device is part of every id, so one device's push can never stand for another's.
        var sink = new Sink();
        var clocks = new DeviceClockStore();
        var mine = await IngestAsync(Device("Canonical"), DeviceDialect.Canonical, Body("c"), Received, sink, clocks);
        var other = Device("Canonical") with { DeviceId = Guid.Parse("0199a000-0000-7000-8000-0000000d0032"), Code = "S-32" };
        var theirs = await IngestAsync(other, DeviceDialect.Canonical, Body("c"), Received, sink, clocks);

        theirs.Select(x => x.Id).Should().NotIntersectWith(mine.Select(x => x.Id));
    }

    public static TheoryData<string, string, string> Mappings => new()
    {
        // A mapping (crossings' time, package, track id, send time) and what the kit says about it: nothing, or a fragment
        // of its warning.
        { "crossings stamped at receipt without a package", "received", "counted again" },
        { "crossings stamped at receipt with a package", "received package", null },
        { "anonymous crossings with a time, no package and no send time", "timed", "never" },
        { "anonymous crossings with a time and a send time, no package", "timed sent", "unchanged send time" },
        { "tracked crossings with a time, no package", "timed track", null },
        { "anonymous crossings with a time and a package", "timed package", null }
    };

    [Theory]
    [MemberData(nameof(Mappings))]
    public void Warnings_Should_NameCrossingsAResendCannotBeRecognisedBy(string because, string shape, string warning)
    {
        bool Has(string part) => shape.Split(' ').Contains(part);
        var mapping = DeclarativeMapping.Parse(Encoding.UTF8.GetBytes($$"""
            { "name": "probe-v1", "title": "Warning probe", {{(Has("package") ? "\"package\": \"$.seq\"," : string.Empty)}}
              {{(Has("sent") ? "\"sentTime\": { \"path\": \"$.sent\", \"unit\": \"ms\" }," : string.Empty)}}
              "crossings": { "items": "$.events[*]", "line": "@.line", "direction": { "path": "@.dir", "in": ["in"], "out": ["out"] },
                             {{(Has("track") ? "\"trackId\": \"@.id\"," : string.Empty)}}
                             "time": {{(Has("received") ? "{ \"received\": true }" : "{ \"path\": \"@.at\", \"unit\": \"ms\" }")}} } }
            """), "probe-v1");

        mapping.Warnings.Should().HaveCount(warning is null ? 0 : 1, because);
        if (warning is not null)
            mapping.Warnings[0].Should().Contain(warning).And.Contain("\"package\"");
    }

    [Fact]
    public void Of_Should_GiveTheSameId_When_ThePackageIdsComeInAnotherOrder()
    {
        // Rec 2 of the ARV-174 review: a Xovis body is a list of envelopes, each with its package id; the same envelopes in
        // another order are the same data.
        var device = Guid.Parse("0199a000-0000-7000-8000-0000000d0031");
        var interval = new IntervalCount("Entry A", 5, 0, Received.AddMinutes(-5), Received.AddSeconds(-1));
        MappedPush Push(params long[] packages) => new(Received, packages, [], [], [], [interval], null, 0, []);

        SensingBatchIds.Of(device, PushKinds.Intervals, Push(35, 34), [1, 2], Received)
            .Should().Be(SensingBatchIds.Of(device, PushKinds.Intervals, Push(34, 35), [9, 9], Received.AddSeconds(20)));
        SensingBatchIds.Of(device, PushKinds.Intervals, Push(34), [1, 2], Received).Should().NotBe(SensingBatchIds.Of(device, PushKinds.Intervals, Push(34, 35), [1, 2], Received));
    }
}
