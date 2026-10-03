using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Ariva.Core.Integration;

namespace Ariva.Infra.Integration.Outbound;

/// <summary>
/// The transport of every outbound call (ARV-045, CWE-918): no proxy, no redirects, a connection only to an address the
/// host resolved to in this very call and every one of which is allowed (<see cref="OutboundRules.AddressRefusal"/>), so
/// a DNS answer that changes between a check and the connection (rebinding) cannot reach another address; TLS validated
/// against the system roots or, when the endpoint pins one, only its CA; a client certificate for mutual TLS.
/// </summary>
public static class OutboundTransport
{
    public static SocketsHttpHandler Create(OutboundTarget target, IOutboundResolver resolver, bool allowLoopback, X509Certificate2 clientCertificate)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(resolver);
        var pinned = target.PinnedCaPem is null ? null : X509Certificate2.CreateFromPem(target.PinnedCaPem);
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(target.TimeoutSeconds, 10)),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            MaxResponseHeadersLength = 64,
            MaxConnectionsPerServer = 8,
            ConnectCallback = (context, ct) => ConnectAsync(context.DnsEndPoint, target.Networks, resolver, allowLoopback, ct)
        };
        // The chain is built by the handshake with the intermediates the server sent, never by downloading a missing one
        // (the AIA URL a certificate names would be fetched outside this transport: CWE-918), without revocation fetches
        // for the same reason, and against the pinned CA alone when the endpoint pins one.
        var policy = new X509ChainPolicy { DisableCertificateDownloads = true, RevocationMode = X509RevocationMode.NoCheck };
        if (pinned is not null)
        {
            policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            policy.CustomTrustStore.Add(pinned);
        }

        handler.SslOptions = new SslClientAuthenticationOptions
        {
            CertificateChainPolicy = policy,
            // The operating system's choice of protocol versions (TLS 1.2 and 1.3 on supported systems). Any error (an
            // untrusted chain, a name mismatch, no certificate) refuses the connection.
            RemoteCertificateValidationCallback = (_, _, _, errors) => errors == SslPolicyErrors.None,
            ClientCertificates = clientCertificate is null ? null : [clientCertificate]
        };
        return handler;
    }

    /// <summary>Resolves, refuses unless every address is allowed, then connects to the first that answers.</summary>
    public static async ValueTask<Stream> ConnectAsync(DnsEndPoint endPoint, IReadOnlyList<string> networks, IOutboundResolver resolver, bool allowLoopback, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        var host = endPoint.Host.Trim('[', ']');
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await resolver.ResolveAsync(host, ct);
        if (addresses is null || addresses.Length == 0)
            throw new OutboundRefusedException("The endpoint's host has no address.");
        foreach (var address in addresses)
        {
            if (OutboundRules.AddressRefusal(address, networks, allowLoopback) is { } refusal)
                throw new OutboundRefusedException("Refused to connect: " + refusal);
        }

        Exception last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException e)
            {
                socket.Dispose();
                last = e;
            }
        }

        throw new HttpRequestException("The endpoint did not accept a connection.", last);
    }
}
