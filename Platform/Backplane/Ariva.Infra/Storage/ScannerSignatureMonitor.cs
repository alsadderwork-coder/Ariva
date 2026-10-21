using Ariva.Core.Services.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ariva.Infra.Storage;

/// <summary>
/// The staleness alarm of the malware scanner's signatures (ARV-092c), in Ariva.Api.Main whenever <c>MalwareScan:Host</c>
/// names a scanner: once <see cref="FirstCheckAfter"/> the start (clamd loads its signatures in about 20 seconds) and then
/// every <c>MalwareScan:SignatureCheckMinutes</c>, it asks the scanner for its signature database
/// (<see cref="IMalwareScanner.SignaturesAsync"/>) and reports it (<see cref="UploadScanMetrics"/>).
/// <list type="bullet">
/// <item>Signatures built longer ago than <c>MalwareScan:MaxSignatureAgeHours</c>: security event 9133
/// (<see cref="SignaturesStale"/>, Warning) with their age, the limit, the engine and database versions and the build time,
/// when they become stale and again at most every <see cref="RepeatEvery"/> while they stay so; an Information line when
/// they are fresh again.</item>
/// <item>No answer (or one not understood, or a build time more than <see cref="FutureTolerance"/> ahead, which would
/// silence the alarm): no event, since event 9132 covers the uploads refused meanwhile and the answer gauge reads 0, but a
/// Warning line throttled the same way, and an Information line when the scanner answers again.</item>
/// </list>
/// Every clock read is the <see cref="TimeProvider"/>'s; a check that fails is logged and the next one runs, so the alarm
/// never stops the host.
/// </summary>
internal sealed class ScannerSignatureMonitor : BackgroundService
{
    #region Fields

    public static readonly EventId SignaturesStale = new(9133, "SecurityEvent.UploadScanSignaturesStale");

    /// <summary>The first check after the start.</summary>
    public static readonly TimeSpan FirstCheckAfter = TimeSpan.FromMinutes(1);

    /// <summary>The longest a stale or silent scanner goes without its warning being written again.</summary>
    public static readonly TimeSpan RepeatEvery = TimeSpan.FromHours(1);

    /// <summary>
    /// How far ahead of the host's clock a build time may be (a scanner whose clock or time zone is off) before it is taken
    /// as not understood rather than as fresh signatures.
    /// </summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromDays(1);

    /// <summary>The fixed reasons of a check without signatures, as the warning names them.</summary>
    public const string NoAnswer = "NoAnswer";

    public const string BuiltInTheFuture = "BuiltInTheFuture";

    public const string CheckFailed = "CheckFailed";

    private readonly IMalwareScanner _scanner;
    private readonly IOptions<MalwareScanSettings> _settings;
    private readonly UploadScanMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly ILogger<ScannerSignatureMonitor> _logger;
    private readonly TimeSpan _firstCheckAfter;
    private readonly TimeSpan? _interval;

    private bool? _answering;
    private bool? _stale;
    private DateTimeOffset _silentWrittenAt;
    private DateTimeOffset _staleWrittenAt;

    #endregion

    #region Constructors

    public ScannerSignatureMonitor(IMalwareScanner scanner, IOptions<MalwareScanSettings> settings, UploadScanMetrics metrics, TimeProvider timeProvider,
        ILogger<ScannerSignatureMonitor> logger)
        : this(scanner, settings, metrics, timeProvider, logger, FirstCheckAfter, null)
    {
    }

    /// <summary>For tests: a first check and an interval shorter than the settings allow.</summary>
    internal ScannerSignatureMonitor(IMalwareScanner scanner, IOptions<MalwareScanSettings> settings, UploadScanMetrics metrics, TimeProvider timeProvider,
        ILogger<ScannerSignatureMonitor> logger, TimeSpan firstCheckAfter, TimeSpan? interval)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _scanner = scanner;
        _settings = settings;
        _metrics = metrics;
        _time = timeProvider;
        _logger = logger;
        _firstCheckAfter = firstCheckAfter;
        _interval = interval;
    }

    #endregion

    #region Properties

    /// <summary>The time between two checks: <c>MalwareScan:SignatureCheckMinutes</c>, within its bounds.</summary>
    private TimeSpan Interval => _interval ?? TimeSpan.FromMinutes(Math.Clamp(_settings.Value.SignatureCheckMinutes,
        MalwareScanSettings.MinSignatureCheckMinutes, MalwareScanSettings.MaxSignatureCheckMinutes));

    /// <summary>The age past which the signatures are stale: <c>MalwareScan:MaxSignatureAgeHours</c>, within its bounds.</summary>
    private TimeSpan MaxAge => TimeSpan.FromHours(Math.Clamp(_settings.Value.MaxSignatureAgeHours,
        MalwareScanSettings.MaxSignatureAgeHoursFloor, MalwareScanSettings.MaxSignatureAgeHoursCeiling));

    /// <summary>The checks made so far (tests).</summary>
    internal int Checks { get; private set; }

    #endregion

    #region Loop

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_firstCheckAfter, _time, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, _time);
        do
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // a failed check must never stop the host; the next one runs at the next interval
            catch (Exception e)
#pragma warning restore CA1031
            {
                _logger.LogError(e, "The malware scanner's signature check failed; it runs again at the next interval");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    #endregion

    #region Check

    /// <summary>One check: asks the scanner, reports what it said and writes the alarm's lines as their throttles allow.</summary>
    internal async Task CheckAsync(CancellationToken ct)
    {
        Checks++;
        MalwareSignatures signatures;
        Exception failure = null;
        try
        {
            signatures = await _scanner.SignaturesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // the scanner's contract is not to throw; if it does, the check reads as no answer
        catch (Exception e)
#pragma warning restore CA1031
        {
            signatures = null;
            failure = e;
        }

        var now = _time.GetUtcNow();
        var freshFor = Interval * 3;
        var silence = failure is not null ? CheckFailed
            : signatures is null ? NoAnswer
            : signatures.AgeAt(now) < -FutureTolerance ? BuiltInTheFuture
            : null;
        if (silence is not null)
        {
            _metrics.Report(null, freshFor);
            Silent(silence, failure, now);
            return;
        }

        _metrics.Report(signatures, freshFor);
        Answered(signatures, now);
    }

    private void Silent(string reason, Exception failure, DateTimeOffset now)
    {
        if (_answering != false || now - _silentWrittenAt >= RepeatEvery)
        {
            _logger.LogWarning(failure,
                "The malware scanner did not answer its signature check ({Reason}): the age of its signatures is unknown, and outside vm-local uploads are refused while it gives no verdict",
                reason);
            _silentWrittenAt = now;
        }

        _answering = false;
    }

    private void Answered(MalwareSignatures signatures, DateTimeOffset now)
    {
        var age = signatures.AgeAt(now);
        var hours = (long)Math.Floor(Math.Max(0, age.TotalHours));
        var maxAge = MaxAge;
        if (_answering is null)
            _logger.LogInformation("The malware scanner answers: ClamAV {EngineVersion}, signature database {DatabaseVersion} built {SignaturesBuiltUtc:O}, {SignatureAgeHours} hours old",
                signatures.EngineVersion, signatures.DatabaseVersion, signatures.BuiltUtc, hours);
        else if (_answering == false)
            _logger.LogInformation("The malware scanner answers its signature check again");
        _answering = true;

        if (age > maxAge)
        {
            if (_stale != true || now - _staleWrittenAt >= RepeatEvery)
            {
                _logger.LogWarning(SignaturesStale,
                    "The malware scanner's signatures are {SignatureAgeHours} hours old, over MalwareScan:MaxSignatureAgeHours ({MaxSignatureAgeHours}): ClamAV {EngineVersion}, signature database {DatabaseVersion} built {SignaturesBuiltUtc:O}",
                    hours, (long)maxAge.TotalHours, signatures.EngineVersion, signatures.DatabaseVersion, signatures.BuiltUtc);
                _staleWrittenAt = now;
            }

            _stale = true;
            return;
        }

        if (_stale == true)
            _logger.LogInformation("The malware scanner's signatures are fresh again: signature database {DatabaseVersion} built {SignaturesBuiltUtc:O}, {SignatureAgeHours} hours old",
                signatures.DatabaseVersion, signatures.BuiltUtc, hours);
        _stale = false;
    }

    #endregion
}
