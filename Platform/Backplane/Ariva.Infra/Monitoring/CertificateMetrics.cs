using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ariva.Infra.DataProtection;
using Microsoft.Extensions.Hosting;

namespace Ariva.Infra.Monitoring;

/// <summary>
/// Expiry of the certificates the host has loaded (meter <c>Ariva.Certificates</c>, ARV-125a):
/// <see cref="ExpiresInName"/>, the seconds until each one's end of validity (negative once it has passed), per
/// <c>certificate</c>: <see cref="DataProtection"/> (the certificate that protects the key ring now, every host),
/// <see cref="MqttServer"/> (Ingest's MQTT listener, when it runs TLS), since ARV-198 <see cref="PodTls"/> (the certificate an API
/// host's last https handshake presented, k8s-prd) and <see cref="WebTls"/> (api-main only: the web pod's certificate, its public
/// half projected alone from the web pod's secret, which the web pod's reload watcher serves within its interval; a configured file
/// that cannot be read or holds no certificate reads 0, as a certificate at its end, so the alert fires rather than going quiet).
/// Only the end date is kept, read from the certificate the host already loaded for its own use, the one a handshake presented,
/// or the web certificate's public file: no key is read, copied or mounted for this (CWE-269), and nothing else of the certificate
/// (subject, serial) becomes a tag. The ingress certificate is in no Ariva pod; its expiry comes
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

    /// <summary>ARV-198: the certificate the API host's last https handshake presented (pod TLS, k8s-prd).</summary>
    public const string PodTls = "pod-tls";

    /// <summary>ARV-198: the web pod's certificate (its public half; api-main only).</summary>
    public const string WebTls = "web-tls";

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal) { DataProtection, MqttServer, PodTls, WebTls };

    #region Fields

    private readonly Meter _meter;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DateTime> _notAfter = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CertificateFile> _files = new(StringComparer.Ordinal);

    /// <summary>The setting that names the web pod's projected public certificate (api-main, with pod TLS; ARV-198).</summary>
    public const string WebCertificatePathSetting = "Monitoring:WebCertificatePath";

    #endregion

    #region Constructors

    public CertificateMetrics(IMeterFactory meterFactory = null, TimeProvider timeProvider = null, DataProtectionCertificates dataProtection = null,
        Microsoft.Extensions.Configuration.IConfiguration configuration = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _meter.CreateObservableGauge(ExpiresInName, Observe, unit: "s",
            description: "Seconds until the end of validity of each certificate the host has loaded (negative once passed)");
        if (dataProtection?.Current is { } current)
            Track(DataProtection, current.NotAfter.ToUniversalTime());
        if (configuration?[WebCertificatePathSetting] is { Length: > 0 } webCertificate)
            TrackFile(WebTls, webCertificate);
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
        var value = DateTime.SpecifyKind(notAfterUtc, DateTimeKind.Utc);
        // Written only when it changes: pod-tls is tracked on every handshake.
        if (!_notAfter.TryGetValue(certificate, out var current) || current != value)
            _notAfter[certificate] = value;
    }

    /// <summary>
    /// ARV-198: reports one of the <see cref="Known"/> certificates from a PEM file read each time the gauge is collected, through
    /// the kubelet's links (tls.crt, ..data and the dated folder), parsed again only when its content changes, so a renewal the
    /// kubelet swaps in is seen without a restart. A file that is missing, larger than 64 KB or holds no certificate reads 0 (as a
    /// certificate at its end), so certificateExpiry fires on it rather than reading nothing. Only the certificate's end is kept.
    /// </summary>
    public void TrackFile(string certificate, string path)
    {
        if (certificate is null || !Known.Contains(certificate))
            throw new ArgumentOutOfRangeException(nameof(certificate), certificate, "Not a certificate Ariva reports.");
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _files[certificate] = new CertificateFile(path);
    }

    private IEnumerable<Measurement<double>> Observe()
    {
        var unreadable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (certificate, file) in _files)
        {
            if (file.NotAfter() is { } notAfter)
                Track(certificate, notAfter);
            else
            {
                _notAfter.TryRemove(certificate, out _);
                unreadable.Add(certificate);
            }
        }

        var now = _time.GetUtcNow().UtcDateTime;
        return _notAfter.Select(c => (c.Key, Seconds: Math.Round((c.Value - now).TotalSeconds)))
            .Concat(unreadable.Select(certificate => (Key: certificate, Seconds: 0d)))
            .OrderBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => new Measurement<double>(c.Seconds, new KeyValuePair<string, object>("certificate", c.Key)))
            .ToList();
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _meter.Dispose();

    #endregion

    #region Types

    /// <summary>
    /// A certificate file read on each call through its links (a FileStream opens the target: in the kubelet's layout tls.crt links
    /// to ..data/tls.crt, and ..data to the dated folder a renewal replaces, so neither the link's time nor its length says
    /// anything), at most 64 KB measured on the open handle, and parsed again only when its content's hash changes. Null when it
    /// cannot be read or holds no certificate; never throws.
    /// </summary>
    private sealed class CertificateFile(string path)
    {
        private const int MaxBytes = 65_536;
        private readonly Lock _gate = new();
        private byte[] _hash;
        private DateTime? _notAfter;

        public DateTime? NotAfter()
        {
            lock (_gate)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length is <= 0 or > MaxBytes)
                        return Unread();
                    var content = new byte[(int)stream.Length];
                    stream.ReadExactly(content);
                    var hash = SHA256.HashData(content);
                    if (_notAfter is not null && _hash is not null && hash.AsSpan().SequenceEqual(_hash))
                        return _notAfter;
                    using var certificate = X509Certificate2.CreateFromPem(System.Text.Encoding.UTF8.GetString(content));
                    _hash = hash;
                    _notAfter = certificate.NotAfter.ToUniversalTime();
                    return _notAfter;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
                {
                    return Unread();
                }
            }
        }

        private DateTime? Unread()
        {
            _hash = null;
            _notAfter = null;
            return null;
        }
    }

    #endregion
}
