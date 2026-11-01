using System.Net.Security;
using System.Security.Authentication;

namespace Ariva.Infra.Security;

/// <summary>
/// The TLS versions and cipher suites Ariva speaks, in one place for both directions (ASVS V12.1.1, V12.1.2): the pods' https
/// endpoints and Ingest's MQTT listener offer them (ARV-082b, ARV-084, through PodTransportExtensions), every outbound
/// integration call accepts only them (ARV-205, <see cref="Integration.Outbound.OutboundTransport"/>), and so does every other
/// TLS client a host opens (ARV-280): PostgreSQL, Redis, the SMTP relay (<see cref="Apply"/>), Kafka (<see cref="OpenSslCipherList"/>)
/// and the log and telemetry collectors (a copy in Ariva.ServiceDefaults, CollectorTls, held equal by a test). Pinned on purpose
/// rather than left to the operating system: a newer version or suite needs a change here, reviewed with the crypto inventory.
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

    /// <summary>
    /// ARV-280: <see cref="CipherSuites"/>' TLS 1.2 suites by their OpenSSL names, for librdkafka's <c>ssl.cipher.suites</c> (every
    /// Kafka client: Ariva's own and MassTransit's rider), with OpenSSL's security level pinned at 2, which allows no version
    /// below TLS 1.2 whatever the image's OpenSSL configuration says (librdkafka has no minimum version setting). TLS 1.3's three
    /// suites are OpenSSL's default for TLS 1.3, which librdkafka does not let Ariva set: the same three as <see cref="CipherSuites"/>.
    /// </summary>
    public static string OpenSslCipherList { get; } =
        "ECDHE-ECDSA-AES128-GCM-SHA256:ECDHE-ECDSA-AES256-GCM-SHA384:ECDHE-ECDSA-CHACHA20-POLY1305:" +
        "ECDHE-RSA-AES128-GCM-SHA256:ECDHE-RSA-AES256-GCM-SHA384:ECDHE-RSA-CHACHA20-POLY1305:@SECLEVEL=2";

    #endregion

    #region Methods

    /// <summary>
    /// ARV-280: a client handshake's options restricted to <see cref="Protocols"/> and, on Linux, <see cref="CipherSuites"/>: for a
    /// TLS client whose library hands Ariva its SslClientAuthenticationOptions (Npgsql, MailKit). Redis's options are built whole
    /// (RedisSettings.Handshake), the outbound calls' and the collectors' in their handlers.
    /// </summary>
    /// <param name="options">The handshake's options.</param>
    public static void Apply(SslClientAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.EnabledSslProtocols = Protocols;
        options.CipherSuitesPolicy = CipherSuitesPolicy;
    }

    private static CipherSuitesPolicy LinuxPolicy()
    {
        if (!OperatingSystem.IsLinux())
            return null;
        return new CipherSuitesPolicy(CipherSuites);
    }

    #endregion
}
