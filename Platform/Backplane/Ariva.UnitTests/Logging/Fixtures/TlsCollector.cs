using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ariva.UnitTests.Logging.Fixtures;

/// <summary>
/// A collector on loopback over TLS for the collector CA tests (ARV-200): Kestrel with HTTP/1.1 and HTTP/2 (OTLP over gRPC
/// needs HTTP/2), serving a certificate for localhost that <see cref="Authority"/> issued, as a site's internal CA would (or,
/// for the refusals, one for another name, or one an intermediate issued that the collector does not send). It keeps the
/// method and path of every request that got past the TLS handshake and answers 200 with an empty body, so a client that
/// does not trust the certificate never reaches it.
/// </summary>
public sealed class TlsCollector : IAsyncDisposable
{
    #region Fields

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly ConcurrentQueue<byte[]> _bodies = new();

    #endregion

    #region Constructors

    private TlsCollector(WebApplication app, X509Certificate2 authority)
    {
        _app = app;
        Authority = authority;
        _app.Run(async context =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            _bodies.Enqueue(body.ToArray());
            _requests.Enqueue($"{context.Request.Method} {context.Request.Path}");
            context.Response.StatusCode = StatusCodes.Status200OK;
        });
    }

    #endregion

    #region Properties

    /// <summary>The CA that issued the collector's certificate (its public part, as a site gives it).</summary>
    public X509Certificate2 Authority { get; }

    /// <summary>The collector's address under the name its certificate holds, for example <c>https://localhost:41234</c>.</summary>
    public string Address { get; private set; }

    /// <summary>The requests that got past the handshake, as <c>POST /v1/traces</c>.</summary>
    public IReadOnlyList<string> Requests => [.. _requests];

    /// <summary>
    /// True when a request's body held <paramref name="text"/> as UTF-8 bytes: Loki's JSON push and an OTLP protobuf (gRPC frames
    /// included, uncompressed) both carry a log line's text that way.
    /// </summary>
    public bool ReceivedText(string text)
    {
        var wanted = System.Text.Encoding.UTF8.GetBytes(text);
        return _bodies.Any(body => body.AsSpan().IndexOf(wanted) >= 0);
    }

    #endregion

    #region Methods

    /// <summary>
    /// The intermediate CA that issued the certificate of a collector started by <see cref="StartBehindAnIntermediateAsync"/>,
    /// which the collector does not send; null otherwise.
    /// </summary>
    public X509Certificate2 Intermediate { get; private set; }

    /// <summary>The certificate the collector serves (its public part and key).</summary>
    public X509Certificate2 ServerCertificate { get; private set; }

    /// <summary>
    /// Starts a collector whose certificate a new CA named <paramref name="authorityName"/> issued, for
    /// <paramref name="serverName"/> (localhost and 127.0.0.1 by default; another name for the host name refusals).
    /// </summary>
    public static async Task<TlsCollector> StartAsync(string authorityName, CancellationToken cancellationToken, string serverName = "localhost")
    {
        var authority = NewAuthority(authorityName);
        return await StartAsync(authority, null, Issue(authority, serverName, caIssuers: null), cancellationToken);
    }

    /// <summary>
    /// Starts a collector whose certificate for localhost an intermediate CA issued (itself issued by a new CA named
    /// <paramref name="authorityName"/>), naming <paramref name="caIssuers"/> as the address of its issuer (the authority
    /// information access extension), and sends only its own certificate in the handshake, as a collector that leaves out its
    /// intermediate does. The server builds no chain itself (offline), so only a client fetches that address.
    /// </summary>
    public static async Task<TlsCollector> StartBehindAnIntermediateAsync(string authorityName, Uri caIssuers, CancellationToken cancellationToken)
    {
        var authority = NewAuthority(authorityName);
        var intermediate = IssueAuthority(authority, $"{authorityName} intermediate");
        return await StartAsync(authority, intermediate, Issue(intermediate, "localhost", caIssuers), cancellationToken);
    }

    /// <summary>Waits until a request whose line starts with <paramref name="prefix"/> arrived, or the patience ran out.</summary>
    public async Task<bool> ReceivedAsync(string prefix, TimeSpan patience, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + patience;
        while (DateTime.UtcNow < until)
        {
            if (_requests.Any(r => r.StartsWith(prefix, StringComparison.Ordinal)))
                return true;
            await Task.Delay(50, cancellationToken);
        }

        return _requests.Any(r => r.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>Waits until a request's body held <paramref name="text"/> (<see cref="ReceivedText"/>), or the patience ran out.</summary>
    public async Task<bool> ReceivedTextAsync(string text, TimeSpan patience, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + patience;
        while (DateTime.UtcNow < until)
        {
            if (ReceivedText(text))
                return true;
            await Task.Delay(50, cancellationToken);
        }

        return ReceivedText(text);
    }

    /// <summary>
    /// The CA's certificate as a PEM file in the test's own <paramref name="folder"/>, as the chart mounts ca.crt; returns its
    /// path. <paramref name="name"/> is a file name only, never a path, so nothing is written outside that folder.
    /// </summary>
    public static string WritePem(X509Certificate2 authority, DirectoryInfo folder, string name = "ca.crt")
    {
        var file = Path.GetFileName(name);
        if (string.IsNullOrEmpty(file) || file != name)
            throw new ArgumentException($"A file name, not a path: {name}", nameof(name));
        var path = Path.Combine(folder.FullName, file);
        File.WriteAllText(path, authority.ExportCertificatePem());
        return path;
    }

    /// <summary>A CA of its own (self-signed, CA basic constraint, key and CRL signing), as a site's internal CA.</summary>
    public static X509Certificate2 NewAuthority(string name)
    {
        // Not disposed here: the certificate signs the collector's certificate with its key.
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Authority.Dispose();
        Intermediate?.Dispose();
        ServerCertificate.Dispose();
    }

    /// <summary>Serves <paramref name="certificate"/> alone: no chain is built for the handshake (offline, no extra certificates).</summary>
    private static async Task<TlsCollector> StartAsync(X509Certificate2 authority, X509Certificate2 intermediate, X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        var context = SslStreamCertificateContext.Create(certificate, [], offline: true);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
        {
            listen.Protocols = HttpProtocols.Http1AndHttp2;
            listen.UseHttps(new TlsHandshakeCallbackOptions
            {
                OnConnection = _ => ValueTask.FromResult(new SslServerAuthenticationOptions
                {
                    ServerCertificateContext = context,
                    ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11]
                })
            });
        }));
        var collector = new TlsCollector(builder.Build(), authority) { Intermediate = intermediate, ServerCertificate = certificate };
        await collector._app.StartAsync(cancellationToken);
        var port = new Uri(collector._app.Urls.First()).Port;
        collector.Address = $"https://localhost:{port}";
        return collector;
    }

    /// <summary>An intermediate CA from <paramref name="issuer"/>, with its key.</summary>
    private static X509Certificate2 IssueAuthority(X509Certificate2 issuer, string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var issuerKey = issuer.GetECDsaPrivateKey();
        using var issued = request.Create(issuer.SubjectName, X509SignatureGenerator.CreateForECDsa(issuerKey), DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow.AddHours(20), RandomNumberGenerator.GetBytes(16));
        return issued.CopyWithPrivateKey(key);
    }

    /// <summary>
    /// A server certificate from <paramref name="authority"/>, with its key: for localhost and 127.0.0.1, or for another name
    /// alone; naming <paramref name="caIssuers"/> as its issuer's address when given.
    /// </summary>
    private static X509Certificate2 Issue(X509Certificate2 authority, string serverName, Uri caIssuers)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={serverName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(serverName);
        if (serverName == "localhost")
            names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        if (caIssuers is not null)
            request.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(null, [caIssuers.AbsoluteUri]));
        using var issuerKey = authority.GetECDsaPrivateKey();
        using var issued = request.Create(authority.SubjectName, X509SignatureGenerator.CreateForECDsa(issuerKey), DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(12), RandomNumberGenerator.GetBytes(16));
        // Kestrel needs the key with the certificate, in a form the platform can use for TLS (a PFX round trip on Windows).
        return X509CertificateLoader.LoadPkcs12(issued.CopyWithPrivateKey(key).Export(X509ContentType.Pfx), null);
    }

    #endregion
}
