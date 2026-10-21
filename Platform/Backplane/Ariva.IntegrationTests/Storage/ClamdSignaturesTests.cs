using System.Globalization;
using System.Text.RegularExpressions;
using Ariva.Core.Services.Storage;
using Ariva.Infra.Storage;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ariva.IntegrationTests.Storage;

/// <summary>
/// ARV-092c against the pinned ClamAV image (offline, its own signature snapshot): clamd's VERSION answer parses, and names
/// the database and the build time of the image's daily.cvd (its header, read in the container: 18 Oct 2026 06:24 +0000,
/// version 28157); that snapshot is older than the default 48 hours, so the staleness alarm writes security event 9133 with
/// its age and version, which is the point of the alarm for a scanner whose signatures no longer arrive.
/// </summary>
[Collection(ClamdCollection.Name)]
public sealed partial class ClamdSignaturesTests(ClamdFixture clamd)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ClamdScanner Scanner() => new(new MalwareScanSettings { Host = clamd.Host, Port = clamd.Port }, TimeSpan.FromMinutes(1), null);

    // ClamAV-VDB:01 Mar 2026 07-24 +0000:27927:355149:90:...
    [GeneratedRegex(@"\AClamAV-VDB:(?<built>[0-9]{2} [A-Z][a-z]{2} [0-9]{4} [0-9]{2}-[0-9]{2}) \+0000:(?<version>[0-9]+):", RegexOptions.CultureInvariant)]
    private static partial Regex DailyHeader();

    [Fact]
    public async Task SignaturesAsync_Should_NameTheImagesDailyDatabase_When_ClamdAnswersVersion()
    {
        var header = DailyHeader().Match(await clamd.DailyDatabaseHeaderAsync(Ct));
        header.Success.Should().BeTrue("the image's daily.cvd starts with its ClamAV-VDB header");
        var built = DateTime.ParseExact(header.Groups["built"].Value, "dd MMM yyyy HH-mm", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var version = int.Parse(header.Groups["version"].Value, CultureInfo.InvariantCulture);

        var signatures = await Scanner().SignaturesAsync(Ct);

        signatures.Should().NotBeNull("clamd answers VERSION in the format the strict pattern takes");
        signatures.EngineVersion.Should().Be("1.4.6", "the pinned image's engine");
        signatures.DatabaseVersion.Should().Be(version, "the newest database loaded is the daily one");
        signatures.BuiltUtc.Kind.Should().Be(DateTimeKind.Utc);
        signatures.BuiltUtc.Should().BeOnOrAfter(built).And.BeBefore(built.AddMinutes(1), "the header gives the build time to the minute, VERSION to the second");
        signatures.BuiltUtc.Should().Be(new DateTime(2026, 10, 18, 6, 24, 14, DateTimeKind.Utc), "the image is pinned by digest, so its snapshot never changes");
    }

    [Fact]
    public async Task CheckAsync_Should_RaiseTheStalenessAlarm_When_TheImagesSnapshotIsOlderThan48Hours()
    {
        using var metrics = new UploadScanMetrics();
        var log = new Lines();
        var monitor = new ScannerSignatureMonitor(Scanner(), Options.Create(new MalwareScanSettings { Host = clamd.Host, Port = clamd.Port }), metrics,
            TimeProvider.System, log);

        await monitor.CheckAsync(Ct);

        var stale = log.Written.Should().ContainSingle(l => l.EventId == ScannerSignatureMonitor.SignaturesStale,
            "the image's snapshot (18 October 2026) is older than MalwareScan:MaxSignatureAgeHours (48)").Subject;
        stale.Level.Should().Be(LogLevel.Warning);
        stale.Message.Should().Contain("signature database 28157 built 2026-10-18T06:24:14.0000000Z").And.Contain("(48)");
        log.Written.Should().NotContain(l => l.Level == LogLevel.Warning && l.EventId != ScannerSignatureMonitor.SignaturesStale, "clamd answered");
    }

    private sealed class Lines : ILogger<ScannerSignatureMonitor>
    {
        public List<(LogLevel Level, EventId EventId, string Message)> Written { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) =>
            Written.Add((logLevel, eventId, formatter(state, exception)));
    }
}
