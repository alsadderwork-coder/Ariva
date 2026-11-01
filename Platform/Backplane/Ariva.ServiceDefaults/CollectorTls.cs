using System.Net.Security;
using System.Security.Authentication;

namespace Ariva.ServiceDefaults;

/// <summary>
/// ARV-280 (ASVS V12.1.1, V12.1.2): the TLS versions and cipher suites a host's log and telemetry collector clients offer
/// (<see cref="CollectorTrust.Pin"/>). A copy of Ariva.Infra's ModernTls, because this project references no Ariva project
/// (ARV-066); <c>TlsClientTests.CollectorTls_Should_EqualModernTls_When_Compared</c> holds the two equal, so a change to one fails
/// the unit tests until the other follows.
/// </summary>
public static class CollectorTls
{
    #region Properties

    /// <summary>The names of the versions, held as names so the code makes no protocol choice in a literal (CA5398).</summary>
    public static IReadOnlyList<string> ProtocolNames { get; } = ["Tls12", "Tls13"];

    /// <summary>TLS 1.2 and 1.3 as one value.</summary>
    public static SslProtocols Protocols { get; } = Enum.Parse<SslProtocols>(string.Join(", ", ProtocolNames));

    /// <summary>TLS 1.3's three suites, and for TLS 1.2 ECDHE with an AEAD cipher, for ECDSA and RSA certificates alike.</summary>
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

    /// <summary><see cref="CipherSuites"/> as a policy on Linux (where the pods run); null elsewhere, where the platform decides.</summary>
    public static CipherSuitesPolicy CipherSuitesPolicy { get; } = OperatingSystem.IsLinux() ? new CipherSuitesPolicy(CipherSuites) : null;

    #endregion
}
