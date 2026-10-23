using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Ariva.Infra.Settings;

/// <summary>
/// ARV-226 (CWE-918, ASVS 5.0 V12.3.2): a TLS handshake whose chain is built without downloads. The platform's default policy, which
/// a handshake takes when it is given none, downloads a missing intermediate from the address the peer's certificate names, and
/// fetches revocation data, before the peer is authenticated (on Linux, during the handshake); a peer, or a party answering on its
/// address, could make a host ask an internal address. With this policy the chain is built from what the peer sent and the trust
/// the caller chose, and no address a certificate names is asked. Revocation is not checked, as for every connection Ariva opens.
/// </summary>
public static class CertificateDownloads
{
    /// <summary>Client side (PostgreSQL, SMTP): the server's chain downloads nothing; the caller's trust and check are kept.</summary>
    /// <param name="options">The handshake's options.</param>
    public static void Refuse(SslClientAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.CertificateChainPolicy = Refusing(options.CertificateChainPolicy);
        options.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;
    }

    /// <summary>Server side (the MQTT listener): a client certificate's chain downloads nothing; the caller's trust and check are kept.</summary>
    /// <param name="options">The handshake's options.</param>
    public static void Refuse(SslServerAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.CertificateChainPolicy = Refusing(options.CertificateChainPolicy);
        options.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;
    }

    /// <summary><paramref name="policy"/>, or a new one, with downloads and revocation off.</summary>
    private static X509ChainPolicy Refusing(X509ChainPolicy policy)
    {
        policy ??= new X509ChainPolicy();
        policy.DisableCertificateDownloads = true;
        policy.RevocationMode = X509RevocationMode.NoCheck;
        return policy;
    }
}
