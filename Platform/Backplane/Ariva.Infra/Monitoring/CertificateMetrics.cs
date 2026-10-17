using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Ariva.Infra.DataProtection;
using Microsoft.Extensions.Hosting;

namespace Ariva.Infra.Monitoring;

/// <summary>
/// Expiry of the certificates the host has loaded (meter <c>Ariva.Certificates</c>, ARV-125a):
/// <see cref="ExpiresInName"/>, the seconds until each one's end of validity (negative once it has passed), per
/// <c>certificate</c>: <see cref="DataProtection"/> (the certificate that protects the key ring now, every host) and
/// <see cref="MqttServer"/> (Ingest's MQTT listener, when it runs TLS). Only the end date is kept, read from the
/// certificate the host already loaded for its own use: no key is read, copied or mounted for this (CWE-269), and nothing
/// else of the certificate (subject, serial) becomes a tag. The ingress certificate is in no Ariva pod; its expiry comes
/// from the site's collector (ARV-125b). A hosted service only so that the gauge exists from the start.
/// </summary>
public sealed class CertificateMetrics : IHostedService, IDisposable
{
    public const string MeterName = "Ariva.Certificates";
    public const string ExpiresInName = "ariva.certificates.expires_in";

    /// <summary>The Data Protection key-protection certificate in use (ARV-008).</summary>
    public const string DataProtection = "data-protection";

    /// <summary>The MQTT listener's server certificate (ARV-024).</summary>
    public const string MqttServer = "mqtt-server";

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal) { DataProtection, MqttServer };

    #region Fields

    private readonly Meter _meter;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DateTime> _notAfter = new(StringComparer.Ordinal);

    #endregion

    #region Constructors

    public CertificateMetrics(IMeterFactory meterFactory = null, TimeProvider timeProvider = null, DataProtectionCertificates dataProtection = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _meter.CreateObservableGauge(ExpiresInName, Observe, unit: "s",
            description: "Seconds until the end of validity of each certificate the host has loaded (negative once passed)");
        if (dataProtection?.Current is { } current)
            Track(DataProtection, current.NotAfter.ToUniversalTime());
    }

    #endregion

    #region Properties

    /// <summary>This instance's meter (tests listen to it alone).</summary>
    internal Meter Meter => _meter;

    #endregion

    #region Methods

    /// <summary>Records the end of validity of one of the <see cref="Known"/> certificates (the latest loaded wins).</summary>
    public void Track(string certificate, DateTime notAfterUtc)
    {
        if (certificate is null || !Known.Contains(certificate))
            throw new ArgumentOutOfRangeException(nameof(certificate), certificate, "Not a certificate Ariva reports.");
        _notAfter[certificate] = DateTime.SpecifyKind(notAfterUtc, DateTimeKind.Utc);
    }

    private IEnumerable<Measurement<double>> Observe()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return _notAfter.OrderBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => new Measurement<double>(Math.Round((c.Value - now).TotalSeconds), new KeyValuePair<string, object>("certificate", c.Key)))
            .ToList();
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _meter.Dispose();

    #endregion
}
