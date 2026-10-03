using Ariva.Core.Domain.Enums;
using Ariva.Core.Integration;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// A system Ariva calls (ARV-045, docs/architecture/integration.md, Outbound connections): registered by an
/// administrator with a recent second factor, bound to sites, with its base URL, the networks its addresses must be in,
/// the authentication kind and its non-secret settings, the secret material protected with the Data Protection key ring
/// (purpose <c>Ariva.Outbound.v1</c>), timeouts and resilience, and for the ACRIS flight pull its path, interval and
/// pull state. Disabled, never deleted. <see cref="ClientVersion"/> moves with every change so hosts rebuild their client.
/// </summary>
public class OutboundEndpoint : BaseAuditableEntity<OutboundEndpoint>
{
    public const int MaxNameLength = 100;
    public const int MaxSites = 32;

    protected OutboundEndpoint()
    {
    }

    public OutboundEndpoint(string code, string name, OutboundEndpointPurpose purpose, IReadOnlyList<string> sites, OutboundConnection connection, string secretProtected,
        bool hasClientCertificate, DateTime utcNow)
    {
        if (!OutboundRules.IsCode(code))
            throw new ArgumentException("A code is 2 to 24 lower case letters, digits or hyphens.", nameof(code));
        if (!Enum.IsDefined(purpose))
            throw new ArgumentOutOfRangeException(nameof(purpose));
        Id = NewId();
        Code = code;
        Purpose = purpose;
        Status = OutboundEndpointStatus.Active;
        Rename(name);
        Apply(sites, connection);
        SetSecret(secretProtected, hasClientCertificate, utcNow);
    }

    public virtual string Code { get; protected set; }
    public virtual string Name { get; protected set; }
    public virtual OutboundEndpointPurpose Purpose { get; protected set; }
    public virtual OutboundEndpointStatus Status { get; protected set; }

    /// <summary>Bound site codes, space separated.</summary>
    public virtual string SiteCodes { get; protected set; }

    public virtual string BaseUrl { get; protected set; }

    /// <summary>CIDR blocks, space separated: every address the host resolves to must be in one.</summary>
    public virtual string AllowedNetworks { get; protected set; }

    public virtual OutboundAuthKind AuthKind { get; protected set; }
    public virtual string TokenPath { get; protected set; }
    public virtual string ClientId { get; protected set; }
    public virtual string Scope { get; protected set; }
    public virtual string HeaderName { get; protected set; }
    public virtual string KeyId { get; protected set; }
    public virtual bool TotpPerRequest { get; protected set; }
    public virtual string PinnedCaPem { get; protected set; }
    public virtual int TimeoutSeconds { get; protected set; }
    public virtual int RetryCount { get; protected set; }
    public virtual int BreakerFailures { get; protected set; }
    public virtual int BreakSeconds { get; protected set; }
    public virtual string PullPath { get; protected set; }
    public virtual int PollSeconds { get; protected set; }

    /// <summary>The secret material as JSON, protected with the Data Protection purpose <c>Ariva.Outbound.v1</c>.</summary>
    public virtual string SecretProtected { get; protected set; }

    public virtual bool HasClientCertificate { get; protected set; }
    public virtual DateTime SecretChangedUtc { get; protected set; }
    public virtual int ClientVersion { get; protected set; }

    // Pull state (ACRIS): written by the poller with SQL, read here.
    public virtual DateTime? LastPollUtc { get; protected set; }
    public virtual string LastModified { get; protected set; }
    public virtual string LastStatus { get; protected set; }
    public virtual int ConsecutiveFailures { get; protected set; }

    public virtual IReadOnlyList<string> Sites => Split(SiteCodes);
    public virtual IReadOnlyList<string> Networks => Split(AllowedNetworks);

    public virtual void Rename(string name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength || !Components.DisplayText.IsClean(trimmed))
            throw new ArgumentException("A name is 1 to 100 characters of plain text.", nameof(name));
        Name = trimmed;
    }

    /// <summary>Sets the sites and the connection (checked by <see cref="OutboundRules.Check"/>); the kind of authentication cannot change, since the secret is the kind's.</summary>
    public virtual void Apply(IReadOnlyList<string> sites, OutboundConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (sites is null || sites.Count is 0 or > MaxSites || sites.Any(s => !Site.IsValidCode(s)))
            throw new ArgumentException("An endpoint is bound to 1 to 32 sites.", nameof(sites));
        if (Purpose is OutboundEndpointPurpose.AcrisFlights or OutboundEndpointPurpose.AmanFeed && sites.Count != 1)
            throw new ArgumentException("A flight or AMAN pull feeds exactly one site.", nameof(sites));
        if (SecretProtected is not null && connection.AuthKind != AuthKind)
            throw new ArgumentException("The authentication kind of an endpoint does not change; register a new endpoint.", nameof(connection));
        SiteCodes = string.Join(' ', sites.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        BaseUrl = connection.BaseUrl.AbsoluteUri;
        AllowedNetworks = string.Join(' ', connection.Networks);
        AuthKind = connection.AuthKind;
        (TokenPath, ClientId, Scope, HeaderName, KeyId, TotpPerRequest) =
            (connection.TokenPath, connection.ClientId, connection.Scope, connection.HeaderName, connection.KeyId, connection.TotpPerRequest);
        PinnedCaPem = connection.PinnedCaPem;
        (TimeoutSeconds, RetryCount, BreakerFailures, BreakSeconds) = (connection.TimeoutSeconds, connection.RetryCount, connection.BreakerFailures, connection.BreakSeconds);
        (PullPath, PollSeconds) = (connection.PullPath, connection.PollSeconds);
        ClientVersion++;
    }

    public virtual void SetSecret(string secretProtected, bool hasClientCertificate, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretProtected);
        SecretProtected = secretProtected;
        HasClientCertificate = hasClientCertificate;
        SecretChangedUtc = utcNow;
        ClientVersion++;
    }

    public virtual bool Disable()
    {
        if (Status == OutboundEndpointStatus.Disabled)
            return false;
        Status = OutboundEndpointStatus.Disabled;
        ClientVersion++;
        return true;
    }

    public virtual bool Enable()
    {
        if (Status == OutboundEndpointStatus.Active)
            return false;
        Status = OutboundEndpointStatus.Active;
        ClientVersion++;
        return true;
    }

    public virtual bool Serves(string siteCode) => siteCode is not null && System.Linq.Enumerable.Contains(Sites, siteCode, StringComparer.Ordinal);

    private static string[] Split(string value) => string.IsNullOrEmpty(value) ? [] : value.Split(' ');
}
