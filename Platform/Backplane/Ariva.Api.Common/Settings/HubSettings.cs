namespace Ariva.Api.Common.Settings;

/// <summary>
/// The live hub's open connections per replica (ARV-096b, CWE-400, ASVS 5.0 V15.2.2), bound from <c>Security:Hubs</c> and refused at
/// start when out of bounds. Beside the 8 a session may hold (<c>HubSessionRegistry.MaxConnectionsPerSession</c>), one replica holds at
/// most <see cref="MaxConnections"/> in all: a negotiate beyond is answered 429 with Retry-After, and a connection opened without one
/// (a WebSocket that skips negotiate) is closed at its handshake.
/// </summary>
public sealed class HubSettings
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Security:Hubs";

    /// <summary>The default of <see cref="MaxConnections"/>.</summary>
    public const int DefaultMaxConnections = 1000;

    /// <summary>The least <see cref="MaxConnections"/> a host starts with.</summary>
    public const int LeastMaxConnections = 10;

    /// <summary>The most <see cref="MaxConnections"/> a host starts with.</summary>
    public const int MostMaxConnections = 20_000;

    /// <summary>
    /// Open hub connections one replica holds, every session together. Default 1,000 (Proposed): each holds up to 72 KB of buffers
    /// (the hub's 64 KB transport and 8 KB application buffers), so about 70 MB at the most; the pilot profile is about 60 screens.
    /// The load harness measures it (ARV-124).
    /// </summary>
    public int MaxConnections { get; set; } = DefaultMaxConnections;

    /// <summary>Whether <see cref="MaxConnections"/> is within its bounds.</summary>
    public bool IsValid => MaxConnections is >= LeastMaxConnections and <= MostMaxConnections;
}
