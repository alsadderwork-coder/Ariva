namespace Ariva.Api.Ingest.Mqtt;

/// <summary>
/// The MQTT transport of Ariva.Api.Ingest (ARV-024), section <c>Ingest:Mqtt</c>. Off unless enabled. Devices connect
/// with TLS (required everywhere but local development), their code as the user name and their credential as the
/// password, and publish to <c>ariva/v1/devices/&lt;code&gt;/&lt;dialect&gt;</c>; nothing can be subscribed to.
/// </summary>
public sealed class MqttSettings
{
    public const string SectionName = "Ingest:Mqtt";

    public bool Enabled { get; set; }

    /// <summary>The TCP port of the MQTT listener (8883, MQTT over TLS, by default).</summary>
    public int Port { get; set; } = 8883;

    /// <summary>TLS on the listener; can be turned off only in the vm-local environment.</summary>
    public bool RequireTls { get; set; } = true;

    /// <summary>The server certificate and its private key, PEM files (a mounted Kubernetes TLS secret).</summary>
    public string CertificatePath { get; set; }

    public string CertificateKeyPath { get; set; }

    /// <summary>Seconds a client has to finish the TLS handshake and send CONNECT.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>Connection attempts per client address per minute, before any credential is looked at.</summary>
    public int ConnectsPerAddressPerMinute { get; set; } = 30;

    /// <summary>
    /// Concurrent MQTT connections at most (all devices together). Before its CONNECT is accepted a connection holds at
    /// most about 9 KB of unread data; an accepted device can have one packet (260 KB) and the transport's read buffer
    /// (up to 1 MB) waiting, so size it with the pod's memory limit.
    /// </summary>
    public int MaxConnections { get; set; } = 2_000;

    /// <summary>Concurrent MQTT connections from one client address at most (a gateway or NAT may front several devices).</summary>
    public int MaxConnectionsPerAddress { get; set; } = 50;

    public IEnumerable<string> Problems(string environment)
    {
        if (!Enabled)
            yield break;
        if (Port is < 1 or > 65_535)
            yield return "Ingest:Mqtt:Port is from 1 to 65535.";
        if (!RequireTls && !string.Equals(environment, "vm-local", StringComparison.Ordinal))
            yield return "Ingest:Mqtt:RequireTls can be false only in vm-local: device credentials must not cross the network in clear.";
        if (RequireTls && (string.IsNullOrWhiteSpace(CertificatePath) || string.IsNullOrWhiteSpace(CertificateKeyPath)))
            yield return "Ingest:Mqtt:CertificatePath and CertificateKeyPath (PEM) are required with TLS.";
        if (ConnectTimeoutSeconds is < 1 or > 60)
            yield return "Ingest:Mqtt:ConnectTimeoutSeconds is from 1 to 60.";
        if (ConnectsPerAddressPerMinute is < 1 or > 10_000)
            yield return "Ingest:Mqtt:ConnectsPerAddressPerMinute is from 1 to 10000.";
        if (MaxConnections is < 1 or > 100_000)
            yield return "Ingest:Mqtt:MaxConnections is from 1 to 100000.";
        if (MaxConnectionsPerAddress < 1 || MaxConnectionsPerAddress > MaxConnections)
            yield return "Ingest:Mqtt:MaxConnectionsPerAddress is from 1 to MaxConnections.";
    }
}
