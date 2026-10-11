using System.Runtime.CompilerServices;
using Ariva.Core.Queueing;
using Ariva.Core.Queueing.Replay;
using Ariva.Core.Sensing;
using Ariva.Infra.Streaming;
using FluentAssertions;

namespace Ariva.UnitTests.Replay;

/// <summary>
/// ARV-148: a replay runs a site under the lateness and push interval the live stream gives it (<c>Stream:Sites</c>) and
/// names them in its manifest's settings hash, so two replays of a site under different timing never look alike.
/// </summary>
public sealed class ReplayRunnerSettingsTests
{
    #region Fakes

    private static readonly DateTime From = new(2026, 9, 28, 17, 0, 0, DateTimeKind.Utc);

    /// <summary>One queue zone, A-VIS, in profile version 12 of any site.</summary>
    private sealed class OneZone() : ZoneGeometrySource(null)
    {
        public override Task<(int Version, IReadOnlyList<string> Zones)?> QueueZonesAsync(string siteCode, int? version, CancellationToken ct)
        {
            IReadOnlyList<string> zones = ["A-VIS"];
            (int Version, IReadOnlyList<string> Zones)? profile = (12, zones);
            return Task.FromResult(profile);
        }

        public override Task<ZoneGeometry> LoadAsync(string siteCode, string queueZoneName, int? version, CancellationToken ct) =>
            Task.FromResult(new ZoneGeometry(new QueueZoneGeometry(queueZoneName,
                new HashSet<string>(StringComparer.Ordinal) { $"{queueZoneName} entry" },
                new HashSet<string>(StringComparer.Ordinal) { $"{queueZoneName} exit" },
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal)), 12));
    }

    /// <summary>An archive with nothing in it.</summary>
    private sealed class EmptyArchive : ISensingArchive, IDeviceHealthArchive
    {
        public Task<SensingArchiveWrite> WriteAsync(IReadOnlyList<SensingBatch> batches, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> WriteAsync(IReadOnlyList<DeviceHealthReported> reports, CancellationToken ct = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ArchivedSensingEvent> ReadAsync(SensingReplayQuery query, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<ArchivedDeviceHealth> ReadAsync(string siteCode, string queueZoneName, DateTime fromUtc, DateTime toUtc,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>A store that records nothing.</summary>
    private sealed class NoStore() : ReplayStore(null)
    {
        public override Task<string> RecordAsync(ReplayRun run, string requestedBy, CancellationToken ct) => Task.FromResult(new string('f', 64));
    }

    private static ReplayRunner Runner(StreamSettings stream)
    {
        var archive = new EmptyArchive();
        return new ReplayRunner(archive, archive, new OneZone(), new NoStore(), stream: stream);
    }

    private static ReplayRequest Request(string site) => new(site, ["A-VIS"], From, From.AddHours(1), null, "ops@host");

    #endregion

    [Fact]
    public async Task RunAsync_Should_NameTheSitesOwnTiming_When_TheSiteHasItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = new StreamSettings
        {
            Sites = new Dictionary<string, StreamSiteSettings>(StringComparer.Ordinal) { ["DMO"] = new StreamSiteSettings { LatenessSeconds = 150 } }
        };

        var own = await Runner(stream).RunAsync(Request("DMO"), null, ct);
        var other = await Runner(stream).RunAsync(Request("AUH-TA"), null, ct);
        var none = await Runner(null).RunAsync(Request("DMO"), null, ct);

        own.Manifest.SettingsHash.Should().Be(ZoneReplay.SettingsHash(stream.ZoneSettingsFor("DMO", new ZoneProcessorSettings())))
            .And.NotBe(none.Manifest.SettingsHash, "DMO runs with 150 seconds of lateness");
        other.Manifest.SettingsHash.Should().Be(none.Manifest.SettingsHash, "a site without timing of its own runs under the defaults");
        none.Manifest.SettingsHash.Should().Be(ZoneReplay.SettingsHash(new ZoneProcessorSettings()));
        own.RowHash.Should().Be(new string('f', 64));
    }
}
