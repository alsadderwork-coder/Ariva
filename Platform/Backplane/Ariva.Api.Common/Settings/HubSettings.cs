namespace Ariva.Api.Common.Settings;

/// <summary>
/// The live hub's open connections per replica (ARV-096b, CWE-400, ASVS 5.0 V15.2.2), bound from <c>Security:Hubs</c> and refused at
/// start when out of bounds. Beside the 8 a session may hold (<c>HubSessionRegistry.MaxConnectionsPerSession</c>), one replica holds at
/// most <see cref="MaxConnectionsPerAccount"/> of one account (every sign-in of one subject together; ARV-229a) and
/// <see cref="MaxConnections"/> in all: a negotiate beyond either is answered 429 with Retry-After, and a connection opened without one
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

    /// <summary>The default of <see cref="MaxConnectionsPerAccount"/>.</summary>
    public const int DefaultMaxConnectionsPerAccount = 200;

    /// <summary>The least <see cref="MaxConnectionsPerAccount"/> a host starts with: one sign-in's own 8, so a session always has its allowance.</summary>
    public const int LeastMaxConnectionsPerAccount = 8;

    /// <summary>The most <see cref="MaxConnectionsPerAccount"/> a host starts with.</summary>
    public const int MostMaxConnectionsPerAccount = MostMaxConnections;

    /// <summary>
    /// Open hub connections one replica holds, every session together. Default 1,000 (Proposed): each holds up to 72 KB of buffers
    /// (the hub's 64 KB transport and 8 KB application buffers), so about 70 MB at the most; the pilot profile is about 60 screens.
    /// The load harness measures it (ARV-124).
    /// </summary>
    public int MaxConnections { get; set; } = DefaultMaxConnections;

    /// <summary>
    /// Open hub connections one replica holds of one account, every sign-in of the subject together (ARV-229a). Default 200
    /// (Proposed): a wall of screens signs in under one account more than once (the load harness's 150 screens of its full run, the
    /// pilot's 50, all under one account), so the default holds such a wall on one replica, and one account takes at most a fifth of the
    /// default total. A site whose screens sign in each under its own account may lower it to 8, one sign-in's own allowance. Above
    /// <see cref="MaxConnections"/> it never applies: the total refuses first.
    /// </summary>
    public int MaxConnectionsPerAccount { get; set; } = DefaultMaxConnectionsPerAccount;

    /// <summary>Whether <see cref="MaxConnections"/> and <see cref="MaxConnectionsPerAccount"/> are within their bounds.</summary>
    public bool IsValid =>
        MaxConnections is >= LeastMaxConnections and <= MostMaxConnections &&
        MaxConnectionsPerAccount is >= LeastMaxConnectionsPerAccount and <= MostMaxConnectionsPerAccount;
}
