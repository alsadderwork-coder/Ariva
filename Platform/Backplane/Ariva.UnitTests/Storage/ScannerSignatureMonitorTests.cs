using Ariva.Core.Services.Storage;
using Ariva.Infra.Storage;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ariva.UnitTests.Storage;

/// <summary>
/// ARV-092c: the staleness alarm of the malware scanner's signatures, with a manual clock and a scanner of the test's own.
/// Signatures older than MalwareScan:MaxSignatureAgeHours write security event 9133 (Warning, with the age, the limit and the
/// database version) when they become stale and again at most once an hour while they stay so, and an Information line when
/// they are fresh again; a scanner that does not answer writes no event but a Warning line throttled the same way. The gauges
/// say the signatures' age and whether the last check got an answer, and nothing once the checks stop. A check that fails
/// never stops the monitor.
/// </summary>
public sealed class ScannerSignatureMonitorTests : IDisposable
{
    #region Fields

    private static readonly DateTimeOffset Start = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    private readonly ManualClock _clock = new(Start);
    private readonly FakeScanner _scanner = new();
    private readonly CapturedLogger _log = new();
    private readonly UploadScanMetrics _metrics;
    private readonly InstrumentReader _reader;

    #endregion

    #region Setup

    public ScannerSignatureMonitorTests()
    {
        _metrics = new UploadScanMetrics(timeProvider: _clock);
        _reader = new InstrumentReader(_metrics.Meter);
    }

    public void Dispose()
    {
        _reader.Dispose();
        _metrics.Dispose();
    }

    #endregion

    #region Helpers

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ScannerSignatureMonitor Monitor(int maxAgeHours = MalwareScanSettings.DefaultMaxSignatureAgeHours) =>
        new(_scanner, Options.Create(new MalwareScanSettings { Host = "127.0.0.1", MaxSignatureAgeHours = maxAgeHours }), _metrics, _clock, _log);

    /// <summary>Signatures built <paramref name="hours"/> hours before the clock's time now.</summary>
    private MalwareSignatures BuiltHoursAgo(double hours, int database = 27927) =>
        new("1.4.3", database, _clock.GetUtcNow().UtcDateTime.AddHours(-hours));

    private IReadOnlyList<CapturedLogger.Line> Stale => [.. _log.Lines.Where(l => l.EventId == ScannerSignatureMonitor.SignaturesStale)];

    private IReadOnlyList<CapturedLogger.Line> Silent => [.. _log.Lines.Where(l => l.Level == LogLevel.Warning && l.Values.ContainsKey("Reason"))];

    private double? Gauge(string name) => _reader.Observe(name) is [var only] ? only.Value : null;

    /// <summary>Advances the clock by one check interval (the default 10 minutes) and checks.</summary>
    private async Task NextCheckAsync(ScannerSignatureMonitor monitor)
    {
        _clock.Advance(TimeSpan.FromMinutes(MalwareScanSettings.DefaultSignatureCheckMinutes));
        await monitor.CheckAsync(Ct);
    }

    #endregion

    #region Fresh and stale

    [Fact]
    public void SignaturesStale_Should_BeSecurityEvent9133_When_Declared() =>
        ScannerSignatureMonitor.SignaturesStale.Should().Be(new EventId(9133, "SecurityEvent.UploadScanSignaturesStale"));

    [Fact]
    public async Task CheckAsync_Should_ReportTheAgeAndWriteNoEvent_When_TheSignaturesAreFresh()
    {
        var monitor = Monitor();
        _scanner.Answer = BuiltHoursAgo(3);

        await monitor.CheckAsync(Ct);

        Stale.Should().BeEmpty();
        Silent.Should().BeEmpty();
        _log.Lines.Should().ContainSingle(l => l.Level == LogLevel.Information, "the first answer is written once, for the record")
            .Which.Message.Should().Be("The malware scanner answers: ClamAV 1.4.3, signature database 27927 built 2026-10-20T05:00:00.0000000Z, 3 hours old");
        Gauge(UploadScanMetrics.AnsweredName).Should().Be(1);
        Gauge(UploadScanMetrics.SignatureAgeName).Should().Be(3 * 3600);

        _clock.Advance(TimeSpan.FromMinutes(5));
        Gauge(UploadScanMetrics.SignatureAgeName).Should().Be(3 * 3600 + 300, "the age is read when observed, so it grows between checks");
        await NextCheckAsync(monitor);
        _log.Lines.Should().ContainSingle("a fresh scanner that keeps answering writes nothing more");
    }

    [Fact]
    public async Task CheckAsync_Should_WriteEvent9133OnceAndAgainAfterAnHour_When_TheSignaturesStayStale()
    {
        var monitor = Monitor();
        _scanner.Answer = BuiltHoursAgo(50);

        await monitor.CheckAsync(Ct);

        var stale = Stale.Should().ContainSingle().Subject;
        stale.Level.Should().Be(LogLevel.Warning);
        stale.Values["SignatureAgeHours"].Should().Be(50L);
        stale.Values["MaxSignatureAgeHours"].Should().Be(48L);
        stale.Values["DatabaseVersion"].Should().Be(27927);
        stale.Values["EngineVersion"].Should().Be("1.4.3");
        stale.Values["SignaturesBuiltUtc"].Should().Be(new DateTime(2026, 10, 18, 6, 0, 0, DateTimeKind.Utc));
        stale.Message.Should().Be("The malware scanner's signatures are 50 hours old, over MalwareScan:MaxSignatureAgeHours (48): ClamAV 1.4.3, " +
                                  "signature database 27927 built 2026-10-18T06:00:00.0000000Z");
        Gauge(UploadScanMetrics.SignatureAgeName).Should().Be(50 * 3600);

        // Every 10 minutes for the next 50 minutes: still stale, nothing written again.
        for (var i = 0; i < 5; i++)
            await NextCheckAsync(monitor);
        Stale.Should().ContainSingle("at most once an hour while they stay stale");

        await NextCheckAsync(monitor);
        Stale.Should().HaveCount(2, "an hour after the last one");
        Stale[1].Values["SignatureAgeHours"].Should().Be(51L);
        _log.Lines.Where(l => l.Level == LogLevel.Information).Should().ContainSingle("only the first answer's line");
    }

    [Fact]
    public async Task CheckAsync_Should_WriteAnInformationLineOnce_When_TheSignaturesAreFreshAgain()
    {
        var monitor = Monitor();
        _scanner.Answer = BuiltHoursAgo(72);
        await monitor.CheckAsync(Ct);

        _scanner.Answer = BuiltHoursAgo(1, database: 28140);
        await NextCheckAsync(monitor);
        await NextCheckAsync(monitor);

        Stale.Should().ContainSingle();
        _log.Lines.Where(l => l.Message.StartsWith("The malware scanner's signatures are fresh again", StringComparison.Ordinal)).Should().ContainSingle()
            .Which.Should().Match<CapturedLogger.Line>(l => l.Level == LogLevel.Information && (int)l.Values["DatabaseVersion"] == 28140);
        Gauge(UploadScanMetrics.SignatureAgeName).Should().Be(3600 + 1200, "built an hour before the stale check, read twenty minutes after it");

        _scanner.Answer = BuiltHoursAgo(49);
        await NextCheckAsync(monitor);
        Stale.Should().HaveCount(2, "stale again is written at once, as on becoming stale");
    }

    [Theory]
    [InlineData(48, 47.9, false)]
    [InlineData(48, 48.1, true)]
    [InlineData(168, 100, false)]
    [InlineData(6, 7, true)]
    public async Task CheckAsync_Should_TakeTheLimitFromTheSettings_When_ItChecks(int maxAgeHours, double ageHours, bool stale)
    {
        _scanner.Answer = BuiltHoursAgo(ageHours);

        await Monitor(maxAgeHours).CheckAsync(Ct);

        Stale.Should().HaveCount(stale ? 1 : 0);
    }

    #endregion

    #region No answer

    [Fact]
    public async Task CheckAsync_Should_WriteAThrottledWarningAndNoEvent_When_TheScannerDoesNotAnswer()
    {
        var monitor = Monitor();
        _scanner.Answer = null;

        await monitor.CheckAsync(Ct);

        Stale.Should().BeEmpty("event 9132 covers the uploads refused meanwhile");
        var silent = Silent.Should().ContainSingle().Subject;
        silent.EventId.Id.Should().Be(0);
        silent.Values["Reason"].Should().Be(ScannerSignatureMonitor.NoAnswer);
        Gauge(UploadScanMetrics.AnsweredName).Should().Be(0);
        Gauge(UploadScanMetrics.SignatureAgeName).Should().BeNull("the age is unknown while the scanner does not answer");

        for (var i = 0; i < 5; i++)
            await NextCheckAsync(monitor);
        Silent.Should().ContainSingle("at most once an hour while it stays silent");
        await NextCheckAsync(monitor);
        Silent.Should().HaveCount(2);

        _scanner.Answer = BuiltHoursAgo(2);
        await NextCheckAsync(monitor);
        _log.Lines.Should().ContainSingle(l => l.Message == "The malware scanner answers its signature check again" && l.Level == LogLevel.Information);
        Gauge(UploadScanMetrics.AnsweredName).Should().Be(1);
        Gauge(UploadScanMetrics.SignatureAgeName).Should().Be(2 * 3600 + 600, "built two hours before the previous check, ten minutes ago");
    }

    [Fact]
    public async Task CheckAsync_Should_KeepTheStaleThrottle_When_TheScannerFallsSilentInBetween()
    {
        var monitor = Monitor();
        _scanner.Answer = BuiltHoursAgo(60);
        await monitor.CheckAsync(Ct);

        _scanner.Answer = null;
        await NextCheckAsync(monitor);
        _scanner.Answer = BuiltHoursAgo(60);
        await NextCheckAsync(monitor);

        Stale.Should().ContainSingle("the signatures never became fresh, so the hourly limit holds through the silence");
        Silent.Should().ContainSingle();
    }

    [Fact]
    public async Task CheckAsync_Should_TakeABuildTimeFarInTheFutureAsNoAnswer_When_ItWouldSilenceTheAlarm()
    {
        var monitor = Monitor();
        _scanner.Answer = BuiltHoursAgo(-25);

        await monitor.CheckAsync(Ct);

        Silent.Should().ContainSingle().Which.Values["Reason"].Should().Be(ScannerSignatureMonitor.BuiltInTheFuture);
        Gauge(UploadScanMetrics.AnsweredName).Should().Be(0);

        _scanner.Answer = BuiltHoursAgo(-3);
        await NextCheckAsync(monitor);
        Gauge(UploadScanMetrics.AnsweredName).Should().Be(1, "a few hours ahead is a scanner's clock or time zone, within a day");
        Stale.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckAsync_Should_ReadAsNoAnswerAndNotThrow_When_TheScannerThrows()
    {
        var monitor = Monitor();
        _scanner.Throw = new InvalidOperationException("probe");

        await monitor.CheckAsync(Ct);

        var silent = Silent.Should().ContainSingle().Subject;
        silent.Values["Reason"].Should().Be(ScannerSignatureMonitor.CheckFailed);
        silent.Exception.Should().BeOfType<InvalidOperationException>();
        Gauge(UploadScanMetrics.AnsweredName).Should().Be(0);
    }

    [Fact]
    public async Task CheckAsync_Should_Throw_When_TheHostStops()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        _scanner.Throw = new OperationCanceledException(stopping.Token);

        var check = () => Monitor().CheckAsync(stopping.Token);

        await check.Should().ThrowAsync<OperationCanceledException>();
        _log.Lines.Should().BeEmpty();
    }

    #endregion

    #region Gauges

    [Fact]
    public async Task Gauges_Should_ReportNothing_When_NoCheckRanOrTheLastIsOlderThanThreeIntervals()
    {
        var monitor = Monitor();
        Gauge(UploadScanMetrics.AnsweredName).Should().BeNull("nothing before the first check");
        Gauge(UploadScanMetrics.SignatureAgeName).Should().BeNull();

        _scanner.Answer = BuiltHoursAgo(1);
        await monitor.CheckAsync(Ct);
        _clock.Advance(TimeSpan.FromMinutes(30));
        Gauge(UploadScanMetrics.AnsweredName).Should().Be(1, "three intervals of 10 minutes");

        _clock.Advance(TimeSpan.FromSeconds(1));
        Gauge(UploadScanMetrics.AnsweredName).Should().BeNull("a monitor that stopped checking reads as missing");
        Gauge(UploadScanMetrics.SignatureAgeName).Should().BeNull();
    }

    #endregion

    #region Loop

    [Fact]
    public async Task ExecuteAsync_Should_CheckSoonAfterStartAndKeepChecking_When_AChecksScannerThrows()
    {
        _scanner.Throw = new InvalidOperationException("probe");
        _scanner.ThrowOnce = true;
        _scanner.Answer = BuiltHoursAgo(1);
        using var monitor = new ScannerSignatureMonitor(_scanner, Options.Create(new MalwareScanSettings { Host = "127.0.0.1" }), _metrics, _clock, _log,
            TimeSpan.Zero, TimeSpan.FromMilliseconds(50));

        await monitor.StartAsync(Ct);
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            wait.CancelAfter(TimeSpan.FromSeconds(10));
            while (monitor.Checks < 3)
                await Task.Delay(20, wait.Token);
        }

        await monitor.StopAsync(Ct);

        monitor.ExecuteTask.Should().NotBeNull();
        monitor.ExecuteTask!.IsFaulted.Should().BeFalse("a failed check never stops the monitor, nor the host");
        Silent.Should().ContainSingle(l => (string)l.Values["Reason"] == ScannerSignatureMonitor.CheckFailed);
        Gauge(UploadScanMetrics.AnsweredName).Should().Be(1, "the later checks got their answer");
    }

    [Fact]
    public async Task ExecuteAsync_Should_WaitBeforeTheFirstCheck_When_TheHostStarts()
    {
        _scanner.Answer = BuiltHoursAgo(1);
        using var monitor = new ScannerSignatureMonitor(_scanner, Options.Create(new MalwareScanSettings { Host = "127.0.0.1" }), _metrics, _clock, _log);

        await monitor.StartAsync(Ct);
        await Task.Delay(300, Ct);
        await monitor.StopAsync(Ct);

        ScannerSignatureMonitor.FirstCheckAfter.Should().Be(TimeSpan.FromMinutes(1), "clamd loads its signatures in about 20 seconds");
        monitor.Checks.Should().Be(0);
        monitor.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    #endregion

    #region Doubles

    /// <summary>A scanner that answers the signature check as the test says.</summary>
    private sealed class FakeScanner : IMalwareScanner
    {
        public MalwareSignatures Answer { get; set; }

        public Exception Throw { get; set; }

        public bool ThrowOnce { get; set; }

        public Task<MalwareScan> ScanAsync(Stream content, CancellationToken ct) => throw new NotSupportedException();

        public Task<MalwareSignatures> SignaturesAsync(CancellationToken ct)
        {
            if (Throw is { } failure)
            {
                if (ThrowOnce)
                    Throw = null;
                return Task.FromException<MalwareSignatures>(failure);
            }

            return Task.FromResult(Answer);
        }
    }

    private sealed class CapturedLogger : ILogger<ScannerSignatureMonitor>
    {
        private readonly Lock _gate = new();
        private readonly List<Line> _lines = [];

        public IReadOnlyList<Line> Lines
        {
            get
            {
                lock (_gate)
                    return [.. _lines];
            }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object>> pairs ? pairs.ToDictionary(p => p.Key, p => p.Value) : [];
            lock (_gate)
                _lines.Add(new Line(logLevel, eventId, formatter(state, exception), values, exception));
        }

        public sealed record Line(LogLevel Level, EventId EventId, string Message, Dictionary<string, object> Values, Exception Exception);
    }

    #endregion
}
