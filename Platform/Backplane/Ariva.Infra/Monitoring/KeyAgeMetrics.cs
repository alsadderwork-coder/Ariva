using System.Diagnostics.Metrics;
using System.Globalization;
using Ariva.Infra.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Ariva.Infra.Monitoring;

/// <summary>
/// The age of every dated key and credential an Ariva deployment holds (meter <c>Ariva.Keys</c>, ARV-207; crypto inventory
/// section 5 item 9: cryptoperiods were procedures, nothing recorded or alerted on a key's age). Three gauges, each by a bounded
/// <c>kind</c> (constants of this class; nothing of a key, a certificate or a row becomes a tag):
/// <list type="bullet">
/// <item><see cref="AgeName"/>: seconds since the oldest key of the kind still in use was made: a certificate's start of
/// validity, a key ring key's creation, a credential's last issue or change.</item>
/// <item><see cref="RotationDueInName"/>: seconds until that oldest key passes its cryptoperiod (key-management.md section 3.4,
/// <c>Monitoring:Cryptoperiods</c>), negative once it has; only for the kinds rotated by hand that have one. The keyRotationDue
/// alert reads it.</item>
/// <item><see cref="KeyRingName"/>: the Data Protection keys by state at the key lifecycle job's last run: <see cref="Active"/>,
/// <see cref="Kept"/> (expired, within their retention) and <see cref="Held"/> (past their retention and still kept). The keyRingHeld
/// alert reads the held count.</item>
/// <item><see cref="ProblemsName"/> (the review's finding L7): the key lifecycle job's last run on this replica by <c>outcome</c>
/// <see cref="Run"/>: 1 when it failed as a whole (the database out of reach, the purge refused, or its run timeout), 0 when it
/// finished. The keyLifecycleFailed alert reads it, so a job that stopped purging is seen before the held keys or the stale ages are.
/// Since the ARV-211 review (finding N1) also outcomes <see cref="Unsigned"/>, the stored values that need a key without a row
/// signature (written before ARV-211; the upgrade window admits it), and <see cref="RetiredSigner"/>, those that need a key a signing
/// certificate no host has configured signed (a rotation's previous signer removed too early; only that signer admits it), from the
/// upgrade check Ariva.Api.Main and Ariva.Api.Cronz run at start (ARV-083a: the hosts whose login may read the stored values) and the
/// key lifecycle job runs every day (<see cref="KeyRingUpgradeCheck"/>), counted as
/// <see cref="KeyRingUpgradeCheck.Reported"/> gives them: the values whose keys the hosts refuse now, so <see cref="Unsigned"/> is 0
/// while the upgrade window is open. The keyRingUnsigned alert reads both.</item>
/// </list>
/// Every API host reports the certificates it loaded (the Data Protection certificate and the previous ones, the key ring's signing
/// certificate and the previous ones, ARV-211); Ariva.Api.Cronz reports the rest from its key lifecycle run (the key ring and the
/// credentials in the database), for <see cref="RunFreshFor"/>, so take the largest copy, never the sum. Only dates are kept: no key is
/// read for this beyond the certificates the host already loaded (CWE-269). A hosted service only so that the gauges exist from the
/// start, and so that cryptoperiods outside their bounds stop the host.
/// </summary>
public sealed class KeyAgeMetrics : IHostedService, IDisposable
{
    #region Constants

    public const string MeterName = "Ariva.Keys";
    public const string AgeName = "ariva.keys.age";
    public const string RotationDueInName = "ariva.keys.rotation_due_in";
    public const string KeyRingName = "ariva.keyring.keys";
    public const string ProblemsName = "ariva.keyring.problems";

    /// <summary>The outcome of <see cref="ProblemsName"/>: the run as a whole.</summary>
    public const string Run = "run";

    /// <summary>The outcome of <see cref="ProblemsName"/>: the stored values that need a key without a row signature (before ARV-211).</summary>
    public const string Unsigned = "unsigned";

    /// <summary>The outcome of <see cref="ProblemsName"/>: the stored values that need a key a signer no host has configured signed.</summary>
    public const string RetiredSigner = "retired-signer";

    /// <summary>The configuration section of the cryptoperiods, in days.</summary>
    public const string CryptoperiodsSection = "Monitoring:Cryptoperiods";

    /// <summary>K3: the Data Protection certificate in use.</summary>
    public const string DataProtectionCertificate = "data-protection-certificate";

    /// <summary>K3: the oldest previous Data Protection certificate configured (kept to unwrap; destroyed by section 3.8 rule 3).</summary>
    public const string PreviousDataProtectionCertificate = "data-protection-previous-certificate";

    /// <summary>K49 (ARV-211): the key ring's signing certificate in use.</summary>
    public const string SigningCertificate = "data-protection-signing-certificate";

    /// <summary>K49: the oldest previous signing certificate configured (verifies only, during a rotation).</summary>
    public const string PreviousSigningCertificate = "data-protection-previous-signing-certificate";

    /// <summary>K4: the oldest key in the key ring (each is the default for 90 days, then kept for its retention).</summary>
    public const string DataProtectionKey = "data-protection-key";

    /// <summary>K12: the oldest secret of an active integration client.</summary>
    public const string IntegrationClientSecret = "integration-client-secret";

    /// <summary>K13: the oldest TOTP seed of an active integration client.</summary>
    public const string IntegrationClientSeed = "integration-client-seed";

    /// <summary>K14: the oldest credential of a device that holds one.</summary>
    public const string DeviceCredential = "device-credential";

    /// <summary>K16: the oldest credential of a display that holds one.</summary>
    public const string DisplayCredential = "display-credential";

    /// <summary>K17 to K20: the oldest secret of an active outbound endpoint (the partner's cadence: age only).</summary>
    public const string OutboundSecret = "outbound-secret";

    /// <summary>The key ring states of <see cref="KeyRingName"/>.</summary>
    public const string Active = "active";

    /// <inheritdoc cref="Active"/>
    public const string Kept = "expired";

    /// <inheritdoc cref="Active"/>
    public const string Held = "held";

    /// <summary>A day and two hours: the key lifecycle job runs daily, so a run's report stands until the next one is due and a little after.</summary>
    public static readonly TimeSpan RunFreshFor = TimeSpan.FromHours(26);

    /// <summary>
    /// The cryptoperiods, in days, of the kinds rotated by hand (Proposed in key-management.md section 3.4 until the owner confirms
    /// them): the Data Protection and signing certificates 2 years; integration client secrets and seeds, device and display
    /// credentials 1 year. The setting under <see cref="CryptoperiodsSection"/> is the kind's name.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> DefaultCryptoperiodDays = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [DataProtectionCertificate] = 730,
        [SigningCertificate] = 730,
        [IntegrationClientSecret] = 365,
        [IntegrationClientSeed] = 365,
        [DeviceCredential] = 365,
        [DisplayCredential] = 365
    };

    private const int MinCryptoperiodDays = 30;
    private const int MaxCryptoperiodDays = 3650;

    #endregion

    #region Fields

    private readonly Meter _meter;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, DateTime> _loaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _cryptoperiodDays;
    private volatile Report _report;
    private volatile Outcome _outcome;
    private volatile Outcome _unsigned;
    private volatile Outcome _retired;

    #endregion

    private sealed record Report(KeyLifecycleRun Run, DateTimeOffset At, TimeSpan FreshFor);

    private sealed record Outcome(long Count, DateTimeOffset At, TimeSpan FreshFor);

    #region Constructors

    public KeyAgeMetrics(IMeterFactory meterFactory = null, TimeProvider timeProvider = null, DataProtectionCertificates dataProtection = null,
        KeyRingSigning signing = null, IConfiguration configuration = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _cryptoperiodDays = Cryptoperiods(configuration);
        if (dataProtection?.Current is { } current)
        {
            _loaded[DataProtectionCertificate] = current.NotBefore.ToUniversalTime();
            var previous = dataProtection.All.Where(c => !ReferenceEquals(c, current)).Select(c => c.NotBefore.ToUniversalTime()).ToList();
            if (previous.Count > 0)
                _loaded[PreviousDataProtectionCertificate] = previous.Min();
        }

        if (signing?.Current is { } signer)
        {
            _loaded[SigningCertificate] = signer.NotBefore.ToUniversalTime();
            if (signing.Previous.Count > 0)
                _loaded[PreviousSigningCertificate] = signing.Previous.Min(c => c.NotBefore.ToUniversalTime());
        }

        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _meter.CreateObservableGauge(AgeName, ObserveAges, unit: "s",
            description: "Seconds since the oldest key or credential of each kind still in use was made");
        _meter.CreateObservableGauge(RotationDueInName, ObserveDue, unit: "s",
            description: "Seconds until the oldest key or credential of each kind passes its cryptoperiod (negative once it has)");
        _meter.CreateObservableGauge(KeyRingName, ObserveKeyRing, unit: "{key}",
            description: "The Data Protection keys at the key lifecycle job's last run: active, expired within their retention, and held past it");
        _meter.CreateObservableGauge(ProblemsName, ObserveProblems,
            description: "The last key lifecycle run on this replica (run: 1 when it failed as a whole, 0 when it finished) and the stored values that need a key without a row signature (unsigned) or signed by a key ring signer no host has configured (retired-signer)");
    }

    #endregion

    #region Properties

    /// <summary>This instance's meter (tests listen to it alone).</summary>
    internal Meter Meter => _meter;

    #endregion

    #region Methods

    /// <summary>The key lifecycle job's run (Ariva.Api.Cronz): the key ring and the credentials in the database, reported for <paramref name="freshFor"/>.</summary>
    public void Reported(KeyLifecycleRun run, TimeSpan freshFor)
    {
        ArgumentNullException.ThrowIfNull(run);
        var now = _time.GetUtcNow();
        _report = new Report(run, now, freshFor);
        _outcome = new Outcome(0, now, freshFor);
        if (run.Unsigned is { } finding)
            UpgradeCheckReported(finding, freshFor);
    }

    /// <summary>
    /// The upgrade check's counts (ARV-211 review, finding N1): the stored values that need a key without a row signature, reported as
    /// <see cref="Unsigned"/>, and those that need a key a signer no host has configured signed, as <see cref="RetiredSigner"/>, for
    /// <paramref name="freshFor"/>.
    /// </summary>
    public void UpgradeCheckReported(KeyRingUpgradeFinding finding, TimeSpan freshFor)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var now = _time.GetUtcNow();
        _unsigned = new Outcome(Math.Max(0, finding.Unsigned), now, freshFor);
        _retired = new Outcome(Math.Max(0, finding.RetiredSigner), now, freshFor);
    }

    /// <summary>A key lifecycle run that failed as a whole (the review's finding L7): reported as 1 for <paramref name="freshFor"/>.</summary>
    public void RunFailed(TimeSpan freshFor) => _outcome = new Outcome(1, _time.GetUtcNow(), freshFor);

    /// <summary>The cryptoperiod of a kind in days, or null when it has none (the kind's age is reported, its rotation is not due).</summary>
    public int? CryptoperiodDays(string kind) => _cryptoperiodDays.TryGetValue(kind ?? string.Empty, out var days) ? days : null;

    private static Dictionary<string, int> Cryptoperiods(IConfiguration configuration)
    {
        // Configuration keys are matched ignoring case, as configuration is; the kinds keep their own spelling.
        var periods = new Dictionary<string, int>(DefaultCryptoperiodDays, StringComparer.OrdinalIgnoreCase);
        var section = configuration?.GetSection(CryptoperiodsSection);
        if (section is null)
            return periods;
        foreach (var entry in section.GetChildren())
        {
            if (!periods.ContainsKey(entry.Key))
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                    $"{CryptoperiodsSection}:{entry.Key} is not a kind with a cryptoperiod ({string.Join(", ", DefaultCryptoperiodDays.Keys)})."));
            if (!int.TryParse(entry.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days is < MinCryptoperiodDays or > MaxCryptoperiodDays)
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                    $"{CryptoperiodsSection}:{entry.Key} is a whole number of days from {MinCryptoperiodDays} to {MaxCryptoperiodDays:N0}."));
            periods[entry.Key] = days;
        }

        return periods;
    }

    /// <summary>When the oldest key of each kind was made: the certificates this host loaded, and the job's report while it is fresh.</summary>
    private Dictionary<string, DateTime> Made()
    {
        var made = new Dictionary<string, DateTime>(_loaded, StringComparer.Ordinal);
        var report = _report;
        if (report is null || _time.GetUtcNow() - report.At > report.FreshFor)
            return made;
        foreach (var (kind, at) in report.Run.OldestMadeUtc)
        {
            if (Known.Contains(kind))
                made[kind] = at;
        }

        return made;
    }

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        DataProtectionCertificate, PreviousDataProtectionCertificate, SigningCertificate, PreviousSigningCertificate, DataProtectionKey,
        IntegrationClientSecret, IntegrationClientSeed, DeviceCredential, DisplayCredential, OutboundSecret
    };

    private IEnumerable<Measurement<double>> ObserveAges()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return Made().OrderBy(m => m.Key, StringComparer.Ordinal)
            .Select(m => new Measurement<double>(Math.Round((now - m.Value).TotalSeconds), new KeyValuePair<string, object>("kind", m.Key)))
            .ToList();
    }

    private IEnumerable<Measurement<double>> ObserveDue()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return Made().Where(m => _cryptoperiodDays.ContainsKey(m.Key)).OrderBy(m => m.Key, StringComparer.Ordinal)
            .Select(m => new Measurement<double>(Math.Round((m.Value.AddDays(_cryptoperiodDays[m.Key]) - now).TotalSeconds),
                new KeyValuePair<string, object>("kind", m.Key)))
            .ToList();
    }

    private IEnumerable<Measurement<int>> ObserveKeyRing()
    {
        var report = _report;
        if (report is null || _time.GetUtcNow() - report.At > report.FreshFor)
            return [];
        var ring = report.Run.KeyRing;
        return
        [
            new Measurement<int>(ring.Active, new KeyValuePair<string, object>("kind", Active)),
            new Measurement<int>(ring.Kept, new KeyValuePair<string, object>("kind", Kept)),
            new Measurement<int>(ring.Held, new KeyValuePair<string, object>("kind", Held))
        ];
    }

    private IEnumerable<Measurement<long>> ObserveProblems()
    {
        var now = _time.GetUtcNow();
        var measured = new List<Measurement<long>>();
        (Outcome Outcome, string Tag)[] outcomes = [(_outcome, Run), (_unsigned, Unsigned), (_retired, RetiredSigner)];
        foreach (var (outcome, tag) in outcomes)
        {
            if (outcome is not null && now - outcome.At <= outcome.FreshFor)
                measured.Add(new Measurement<long>(outcome.Count, new KeyValuePair<string, object>("outcome", tag)));
        }

        return measured;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _meter.Dispose();

    #endregion
}
