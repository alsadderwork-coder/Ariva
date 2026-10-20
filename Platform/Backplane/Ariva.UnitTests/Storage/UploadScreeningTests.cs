using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Services;
using Ariva.Core.Services.Storage;
using Ariva.Infra.Storage;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Fluentx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Ariva.UnitTests.Storage;

/// <summary>
/// ARV-092b: the screening every upload service calls. A detection is refused with the fixed text, audited in a unit of
/// work of its own (action Upload.MalwareRefused, the kind, the signature, the size and SHA-256, never the file) and logged as
/// security event 9131 with the caller, the kind and the signature only; no verdict outside vm-local is refused 503 and
/// logged as event 9132 with a fixed reason (fail closed); in vm-local it is let through with a warning (fail open), and
/// without a scanner there it is not scanned at all.
/// </summary>
public sealed class UploadScreeningTests
{
    #region Fields

    private static readonly Guid CallerId = Guid.Parse("0199a000-0000-7000-8000-000000009131");

    private readonly Mock<IMalwareScanner> _scanner = new(MockBehavior.Strict);
    private readonly CapturedLogger _log = new();
    private readonly AuditUnits _audit = new();

    #endregion

    #region Helpers

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private UploadScreening Screening(bool vmLocal = false, string host = "clamd", IMalwareScanner scanner = null, long maxStreamBytes = MalwareScanSettings.DefaultMaxStreamBytes)
    {
        var services = new ServiceCollection();
        services.AddScoped<IUnitOfWork>(_ => _audit.Next());
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var caller = new Mock<ICurrentUser>();
        caller.SetupGet(c => c.Id).Returns(CallerId);
        caller.SetupGet(c => c.UserName).Returns("it-admin");
        caller.Setup(c => c.GetCallerIpAddress()).Returns("10.1.2.3");
        return new UploadScreening(scanner ?? _scanner.Object, Options.Create(new MalwareScanSettings { Host = host, MaxStreamBytes = maxStreamBytes }),
            UploadScanPolicy.For(vmLocal ? "vm-local" : "k8s-dev"), caller.Object, new ManualClock(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero)),
            scopes, _log);
    }

    private void Answers(MalwareScan scan) =>
        _scanner.Setup(s => s.ScanAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns(async (Stream content, CancellationToken ct) =>
            {
                // As a scanner does: the content is read to its end.
                await content.CopyToAsync(Stream.Null, ct);
                return scan;
            });

    #endregion

    #region Detections

    [Fact]
    public async Task ScreenAsync_Should_RefuseAuditAndLogEvent9131_When_TheScannerFindsAThreat()
    {
        Answers(MalwareScan.Infected(Eicar.Signature));
        var file = Eicar.Bytes();

        var result = await Screening().ScreenAsync(UploadKind.FloorPlan, file, Ct);

        result.ErrorMessages.Should().Equal(UploadScanErrors.Malware);
        var unit = _audit.Units.Should().ContainSingle("the entry is written in a unit of work of its own").Subject;
        unit.Committed.Should().BeTrue("promised and ended, whatever the upload's own unit of work does");
        var entry = unit.Saved.Should().ContainSingle().Subject;
        entry.Action.Should().Be(UploadScreening.RefusedAction);
        entry.TargetType.Should().Be("FloorPlan");
        entry.TargetName.Should().Be(Eicar.Signature);
        entry.ActorId.Should().Be(CallerId);
        using var summary = JsonDocument.Parse(entry.AfterSummary);
        summary.RootElement.GetProperty("kind").GetString().Should().Be("FloorPlan");
        summary.RootElement.GetProperty("signature").GetString().Should().Be(Eicar.Signature);
        summary.RootElement.GetProperty("bytes").GetInt64().Should().Be(file.Length);
        summary.RootElement.GetProperty("sha256").GetString().Should().Be(Convert.ToHexStringLower(SHA256.HashData(file)));
        entry.AfterSummary.Should().NotContain("EICAR-STANDARD", "never the file");

        var line = _log.Lines.Should().ContainSingle(l => l.EventId.Id == 9131).Subject;
        line.EventId.Name.Should().Be("SecurityEvent.UploadMalwareRefused");
        line.Level.Should().Be(LogLevel.Warning);
        line.Values.Keys.Where(k => k != "{OriginalFormat}").Should().BeEquivalentTo(["UploadKind", "UserId", "Signature"], "the caller, the kind and the signature only");
        line.Values["UserId"].Should().Be(CallerId);
        line.Values["Signature"].Should().Be(Eicar.Signature);
    }

    [Theory]
    [InlineData(UploadKind.FlightSchedule)]
    [InlineData(UploadKind.DeviceImport)]
    [InlineData(UploadKind.DeskCodeMappingImport)]
    [InlineData(UploadKind.SiteConfiguration)]
    [InlineData(UploadKind.AdvertisingMedia)]
    public async Task ScreenAsync_Should_AuditUnderTheUploadKind_When_AStreamIsRefused(UploadKind kind)
    {
        Answers(MalwareScan.Infected(Eicar.Signature));
        var file = Encoding.ASCII.GetBytes("code,family\r\n" + Eicar.Text());

        var result = await Screening().ScreenAsync(kind, new MemoryStream(file), Ct);

        result.ErrorMessages.Should().Equal(UploadScanErrors.Malware);
        var entry = _audit.Units.Single().Saved.Single();
        entry.TargetType.Should().Be(kind.ToString());
        using var summary = JsonDocument.Parse(entry.AfterSummary);
        summary.RootElement.GetProperty("bytes").GetInt64().Should().Be(file.Length, "what the scanner read, counted on the way");
        summary.RootElement.GetProperty("sha256").GetString().Should().Be(Convert.ToHexStringLower(SHA256.HashData(file)));
    }

    [Fact]
    public async Task ScreenAsync_Should_NeverWriteAHostileName_When_AScannerHandsOneOn()
    {
        // A scanner of another make that did not pass its name through the pattern: the screening applies it again.
        _scanner.Setup(s => s.ScanAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MalwareScan(MalwareVerdict.Infected, FakeClamd.HostileName, null));

        var result = await Screening().ScreenAsync(UploadKind.AdvertisingMedia, [1, 2, 3], Ct);

        result.ErrorMessages.Should().Equal(UploadScanErrors.Malware);
        _audit.Units.Single().Saved.Single().TargetName.Should().Be(MalwareScan.UnrecognisedSignature);
        _audit.Units.Single().Saved.Single().AfterSummary.Should().NotContain("forged");
        _log.Lines.Should().OnlyContain(l => !l.Message.Contains("forged", StringComparison.Ordinal) && !l.Message.Contains('\n', StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScreenAsync_Should_StillRefuse_When_TheAuditEntryCannotBeWritten()
    {
        Answers(MalwareScan.Infected(Eicar.Signature));
        _audit.FailCommit = true;

        var result = await Screening().ScreenAsync(UploadKind.FloorPlan, Eicar.Bytes(), Ct);

        result.ErrorMessages.Should().Equal(UploadScanErrors.Malware);
        _audit.Units.Single().RolledBack.Should().BeTrue();
        _log.Lines.Should().Contain(l => l.Level == LogLevel.Error && l.Message.Contains("could not be written", StringComparison.Ordinal));
        _log.Lines.Should().Contain(l => l.EventId.Id == 9131);
    }

    #endregion

    #region Clean and no verdict

    [Fact]
    public async Task ScreenAsync_Should_LetTheUploadThroughWithoutAudit_When_ItIsClean()
    {
        Answers(MalwareScan.Clean);

        var result = await Screening().ScreenAsync(UploadKind.FloorPlan, "<svg/>"u8.ToArray(), Ct);

        result.HasErrors.Should().BeFalse();
        _audit.Units.Should().BeEmpty();
        _log.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task ScreenAsync_Should_Refuse503AndLogEvent9132_When_NoVerdictOutsideVmLocal()
    {
        Answers(MalwareScan.Unavailable(MalwareScanReasons.ConnectFailed));

        var result = await Screening(vmLocal: false).ScreenAsync(UploadKind.SiteConfiguration, [1, 2, 3], Ct);

        result.ErrorMessages.Should().Equal(UploadScanErrors.ScannerUnavailable);
        _audit.Units.Should().BeEmpty("no threat was found");
        var line = _log.Lines.Should().ContainSingle().Subject;
        line.EventId.Should().Be(new EventId(9132, "SecurityEvent.UploadScanUnavailable"));
        line.Values["Reason"].Should().Be(MalwareScanReasons.ConnectFailed);
        line.Values["UploadKind"].Should().Be(UploadKind.SiteConfiguration);
    }

    [Fact]
    public async Task ScreenAsync_Should_FailClosed_When_NoScannerIsConfiguredOutsideVmLocal()
    {
        var unconfigured = new ClamdScanner(new MalwareScanSettings(), null, null);

        var result = await Screening(vmLocal: false, host: string.Empty, scanner: unconfigured).ScreenAsync(UploadKind.FloorPlan, [1, 2, 3], Ct);

        result.ErrorMessages.Should().Equal(UploadScanErrors.ScannerUnavailable);
        _log.Lines.Single().Values["Reason"].Should().Be(MalwareScanReasons.NotConfigured);
    }

    [Fact]
    public async Task ScreenAsync_Should_FailOpenWithAWarning_When_NoVerdictInVmLocal()
    {
        Answers(MalwareScan.Unavailable(MalwareScanReasons.TimedOut));

        var result = await Screening(vmLocal: true).ScreenAsync(UploadKind.DeviceImport, [1, 2, 3], Ct);

        result.HasErrors.Should().BeFalse("vm-local only: developer runs without a scanner keep working");
        var line = _log.Lines.Should().ContainSingle().Subject;
        line.Level.Should().Be(LogLevel.Warning);
        line.EventId.Id.Should().Be(0, "not a security event");
        line.Values["Reason"].Should().Be(MalwareScanReasons.TimedOut);
    }

    [Fact]
    public async Task ScreenAsync_Should_NotScan_When_NoScannerIsConfiguredInVmLocal()
    {
        var result = await Screening(vmLocal: true, host: string.Empty).ScreenAsync(UploadKind.FloorPlan, [1, 2, 3], Ct);

        result.HasErrors.Should().BeFalse();
        _scanner.Verify(s => s.ScanAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
        _log.Lines.Should().BeEmpty("the host said so once when it started");
    }

    [Fact]
    public async Task ScreenAsync_Should_StillRefuseADetection_When_InVmLocal()
    {
        Answers(MalwareScan.Infected(Eicar.Signature));

        var result = await Screening(vmLocal: true).ScreenAsync(UploadKind.FloorPlan, Eicar.Bytes(), Ct);

        result.ErrorMessages.Should().Equal([UploadScanErrors.Malware], "only a missing verdict is let through in vm-local");
    }

    [Fact]
    public async Task ScreenAsync_Should_RefuseWithoutScanning_When_AStreamIsOverTheLimit()
    {
        var result = await Screening(maxStreamBytes: 10).ScreenAsync(UploadKind.FloorPlan, new byte[11], Ct);

        result.ErrorMessages.Should().Equal(UploadScanErrors.ScannerUnavailable);
        _scanner.Verify(s => s.ScanAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
        _log.Lines.Single().Values["Reason"].Should().Be(MalwareScanReasons.TooLarge);
    }

    #endregion

    #region One deadline for the parts of an upload

    /// <summary>A scanner that takes <paramref name="each"/> per part (honouring the token) and finds the test file.</summary>
    private void Slow(TimeSpan each, List<int> calls) =>
        _scanner.Setup(s => s.ScanAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns(async (Stream content, CancellationToken ct) =>
            {
                lock (calls)
                    calls.Add(calls.Count + 1);
                using var copy = new MemoryStream();
                await content.CopyToAsync(copy, ct);
                await Task.Delay(each, ct);
                return Eicar.IsIn(copy.ToArray()) ? MalwareScan.Infected(Eicar.Signature) : MalwareScan.Clean;
            });

    private static List<byte[]> Parts(int count) => [.. Enumerable.Range(0, count).Select(i => new[] { (byte)i })];

    [Fact]
    public async Task ScreenAllAsync_Should_RefuseAsNoVerdictAtTheBundlesDeadline_When_ThePartsTogetherTakeLonger()
    {
        // Ten parts of 200 ms: each within any budget of its own, two seconds together. One deadline of 500 ms for them all.
        var calls = new List<int>();
        Slow(TimeSpan.FromMilliseconds(200), calls);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await Screening().ScreenAllAsync(UploadKind.SiteConfiguration, Parts(10), TimeSpan.FromMilliseconds(500), Ct);

        result.ErrorMessages.Should().Equal(UploadScanErrors.ScannerUnavailable);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1.5), "the deadline is the bundle's, not each part's");
        calls.Count.Should().BeLessThan(5, "no part is scanned after the deadline");
        var line = _log.Lines.Should().ContainSingle().Subject;
        line.EventId.Id.Should().Be(9132);
        line.Values["Reason"].Should().Be(MalwareScanReasons.TimedOut);
        _audit.Units.Should().BeEmpty();
    }

    [Fact]
    public async Task ScreenAllAsync_Should_TakeTheRestUnscannedWithOneWarning_When_TheDeadlinePassesInVmLocal()
    {
        var calls = new List<int>();
        Slow(TimeSpan.FromMilliseconds(200), calls);

        var result = await Screening(vmLocal: true).ScreenAllAsync(UploadKind.SiteConfiguration, Parts(10), TimeSpan.FromMilliseconds(500), Ct);

        result.HasErrors.Should().BeFalse("vm-local only");
        calls.Count.Should().BeLessThan(5);
        _log.Lines.Should().ContainSingle().Which.Values["Reason"].Should().Be(MalwareScanReasons.TimedOut);
    }

    [Fact]
    public async Task ScreenAllAsync_Should_ScanEveryPart_When_TheyFitTheDeadline()
    {
        var calls = new List<int>();
        Slow(TimeSpan.Zero, calls);

        var result = await Screening().ScreenAllAsync(UploadKind.SiteConfiguration, Parts(8), TimeSpan.FromSeconds(10), Ct);

        result.HasErrors.Should().BeFalse();
        calls.Count.Should().Be(8);
        _log.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task ScreenAllAsync_Should_StopAtTheFirstDetection_When_APartHoldsAThreat()
    {
        var calls = new List<int>();
        Slow(TimeSpan.Zero, calls);
        var parts = Parts(6);
        parts[2] = Eicar.Bytes();

        var result = await Screening().ScreenAllAsync(UploadKind.SiteConfiguration, parts, TimeSpan.FromSeconds(10), Ct);

        result.ErrorMessages.Should().Equal(UploadScanErrors.Malware);
        calls.Count.Should().Be(3, "the parts after the refused one are not scanned");
        _audit.Units.Single().Saved.Single().TargetType.Should().Be("SiteConfiguration");
    }

    [Fact]
    public async Task ScreenAllAsync_Should_Throw_When_TheCallerCancels()
    {
        Slow(TimeSpan.FromSeconds(30), []);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        caller.CancelAfter(TimeSpan.FromMilliseconds(200));

        var screen = () => Screening().ScreenAllAsync(UploadKind.SiteConfiguration, Parts(3), TimeSpan.FromSeconds(10), caller.Token);

        await screen.Should().ThrowAsync<OperationCanceledException>("the request went away; that is not a verdict");
        _log.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task ScreenAllAsync_Should_UseTheScanTimeoutAsTheBundlesDeadline_When_CalledWithoutOne()
    {
        // Through a real clamd client against a clamd that answers after 30 s: the bundle's three parts get one 5 s budget
        // (MalwareScan:TimeoutSeconds at its least), not 5 s each.
        await using var clamd = new FakeClamd { Mode = FakeClamd.Behaviour.Delay, Delay = TimeSpan.FromSeconds(30) };
        var settings = new MalwareScanSettings { Host = clamd.Host, Port = clamd.Port, TimeoutSeconds = MalwareScanSettings.MinTimeoutSeconds };
        var screening = new UploadScreening(new ClamdScanner(Options.Create(settings)), Options.Create(settings), UploadScanPolicy.For("k8s-dev"),
            new Mock<ICurrentUser>().Object, TimeProvider.System, new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _log);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await screening.ScreenAllAsync(UploadKind.SiteConfiguration, Parts(3), Ct);

        result.ErrorMessages.Should().Equal(UploadScanErrors.ScannerUnavailable);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(9), "one budget for the bundle, not three");
        _log.Lines.Single().Values["Reason"].Should().Be(MalwareScanReasons.TimedOut);
        clamd.Connections.Should().Be(1, "the first part used the whole budget");
    }

    #endregion

    #region Fakes

    /// <summary>The units of work the screening's own scopes get: what each saved, and whether it committed.</summary>
    private sealed class AuditUnits
    {
        private readonly List<AuditUnit> _units = [];

        public bool FailCommit { get; set; }

        public IReadOnlyList<AuditUnit> Units => _units;

        public AuditUnit Next()
        {
            var unit = new AuditUnit(FailCommit);
            _units.Add(unit);
            return unit;
        }
    }

    private sealed class AuditUnit : IUnitOfWork
    {
        private readonly bool _failCommit;
        private bool _promised;
        private bool _transaction;

        public AuditUnit(bool failCommit)
        {
            _failCommit = failCommit;
            var storage = new Mock<IStorageProvider>();
            storage.Setup(s => s.IsTransactionActive()).Returns(() => _transaction);
            storage.Setup(s => s.BeginTransaction(It.IsAny<IsolationLevel>())).Callback(() => _transaction = true);
            storage.Setup(s => s.SaveAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((AuditEntry entry, CancellationToken _) =>
                {
                    Saved.Add(entry);
                    return entry;
                });
            StorageProvider = storage.Object;
        }

        public List<AuditEntry> Saved { get; } = [];

        public bool Committed { get; private set; }

        public bool RolledBack { get; private set; }

        public Guid Id { get; } = Guid.NewGuid();

        public IStorageProvider StorageProvider { get; }

        public bool IsToBeCommitted => _promised;

        public Task EndAsync(CancellationToken ct = default)
        {
            if (_failCommit)
                throw new InvalidOperationException("The database is down (test).");
            Committed = _promised;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken ct = default)
        {
            RolledBack = true;
            return Task.CompletedTask;
        }

        public void RegisterPostCommitAction(Func<Task> action)
        {
        }

        public void RegisterPostRollbackAction(Func<Task> action)
        {
        }

        public void PromiseToCommit() => _promised = true;

        public void PromiseNotToCommit() => _promised = false;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturedLogger : ILogger<UploadScreening>
    {
        public List<Line> Lines { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object>> pairs ? pairs.ToDictionary(p => p.Key, p => p.Value) : [];
            Lines.Add(new Line(logLevel, eventId, formatter(state, exception), values));
        }

        public sealed record Line(LogLevel Level, EventId EventId, string Message, Dictionary<string, object> Values);
    }

    #endregion
}
