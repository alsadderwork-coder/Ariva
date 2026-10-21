using System.Diagnostics.Metrics;
using Ariva.Core.Services.Storage;

namespace Ariva.Infra.Storage;

/// <summary>
/// The malware scanner's signatures (meter <c>Ariva.Uploads</c>, ARV-092c), from the signature check of
/// <see cref="ScannerSignatureMonitor"/>: <see cref="SignatureAgeName"/>, the seconds since the build time of the signature
/// database the scanner reported at the last check that got an answer (read when observed, so it grows between checks), and
/// <see cref="AnsweredName"/>, 1 when the last check got an answer the strict pattern accepted, 0 when it did not (no
/// answer, a reply not understood, a build time in the future). A check is reported for <c>freshFor</c> (three check
/// intervals): a monitor that stopped checking reads as missing, and while the scanner gives no answer the age is not
/// reported at all. Ariva.Api.Main only, one copy per pod (each with its own scanner on its loopback): take the largest age
/// and the smallest answer, never a sum. No tags: the database version goes to the log only.
/// </summary>
public sealed class UploadScanMetrics : IDisposable
{
    public const string MeterName = "Ariva.Uploads";
    public const string SignatureAgeName = "ariva.uploads.scanner.signature_age";
    public const string AnsweredName = "ariva.uploads.scanner.answered";

    #region Fields

    private readonly Meter _meter;
    private readonly TimeProvider _time;
    private volatile Check _last;

    #endregion

    private sealed record Check(MalwareSignatures Signatures, DateTimeOffset At, TimeSpan FreshFor);

    #region Constructors

    public UploadScanMetrics(IMeterFactory meterFactory = null, TimeProvider timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _meter.CreateObservableGauge(SignatureAgeName, ObserveAge, unit: "s",
            description: "Seconds since the build time of the malware scanner's signature database, as it reported it at the last check that got an answer");
        _meter.CreateObservableGauge(AnsweredName, ObserveAnswered,
            description: "Whether the malware scanner answered the last signature check: 1 yes, 0 no (no answer, or an answer not understood)");
    }

    #endregion

    #region Properties

    /// <summary>This instance's meter (tests listen to it alone).</summary>
    internal Meter Meter => _meter;

    #endregion

    #region Methods

    /// <summary>Reports a check made now: the signatures it got, or null for no answer; valid for <paramref name="freshFor"/>.</summary>
    public void Report(MalwareSignatures signatures, TimeSpan freshFor) => _last = new Check(signatures, _time.GetUtcNow(), freshFor);

    private Check Fresh()
    {
        var last = _last;
        return last is not null && _time.GetUtcNow() - last.At <= last.FreshFor ? last : null;
    }

    private IEnumerable<Measurement<double>> ObserveAge() =>
        Fresh() is { Signatures: { } signatures }
            ? [new Measurement<double>(Math.Round(signatures.AgeAt(_time.GetUtcNow()).TotalSeconds))]
            : [];

    private IEnumerable<Measurement<int>> ObserveAnswered() =>
        Fresh() is { } last ? [new Measurement<int>(last.Signatures is null ? 0 : 1)] : [];

    public void Dispose() => _meter.Dispose();

    #endregion
}
