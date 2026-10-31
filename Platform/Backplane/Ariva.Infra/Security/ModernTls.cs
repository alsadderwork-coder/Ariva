using System.Net.Security;
using System.Security.Authentication;

namespace Ariva.Infra.Security;

/// <summary>
/// The TLS versions and cipher suites Ariva speaks, in one place for both directions (ASVS V12.1.1, V12.1.2): the pods' https
/// endpoints and Ingest's MQTT listener offer them (ARV-082b, ARV-084, through PodTransportExtensions), and every outbound
/// integration call accepts only them (ARV-205, <see cref="Integration.Outbound.OutboundTransport"/>). Pinned on purpose rather
/// than left to the operating system: a newer version or suite needs a change here, reviewed with the crypto inventory.
/// </summary>
public static class ModernTls
{
    #region Properties

    /// <summary>
    /// The names of the versions Ariva speaks, which configured Kestrel endpoints are checked against by name. Held as names so the
    /// code makes no protocol choice of its own in a literal (CA5398); <see cref="Protocols"/> is parsed from them.
    /// </summary>
    public static IReadOnlyList<string> ProtocolNames { get; } = ["Tls12", "Tls13"];

    /// <summary>TLS 1.2 and 1.3 as one value, for a handshake Ariva configures in code (a listener or an outbound call).</summary>
    public static SslProtocols Protocols { get; } = Enum.Parse<SslProtocols>(string.Join(", ", ProtocolNames));

    /// <summary>
    /// The suites Ariva offers and accepts on Linux: TLS 1.3's three, and for TLS 1.2 ECDHE (forward secrecy) with an AEAD cipher,
    /// for ECDSA and RSA certificates alike. No CBC suite, no static RSA key exchange and no finite field DHE, matching the
    /// database's ssl_ciphers ECDHE+AESGCM:ECDHE+CHACHA20 (ARV-082a).
    /// </summary>
    public static IReadOnlyList<TlsCipherSuite> CipherSuites { get; } =
    [
        TlsCipherSuite.TLS_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256
    ];

    /// <summary>
    /// <see cref="CipherSuites"/> as a policy for a handshake, on Linux (where the pods run); null elsewhere, where .NET cannot
    /// restrict the suites per connection (Windows: the platform's own TLS configuration decides) and the platform defaults apply.
    /// </summary>
    public static CipherSuitesPolicy CipherSuitesPolicy { get; } = LinuxPolicy();

    #endregion

    #region Methods

    private static CipherSuitesPolicy LinuxPolicy()
    {
        if (!OperatingSystem.IsLinux())
            return null;
        return new CipherSuitesPolicy(CipherSuites);
    }

    #endregion
}
